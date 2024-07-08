using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.utils;

namespace Bots;

//class to implement single observer information set monte carlo tree search. This implementation only 
//searches a tree of moves for the current player's turn, so the observer is the current player.
public class SOISMCTS : AI
{
    //parameters for computational budget
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private readonly TimeSpan _turnTimeout = TimeSpan.FromSeconds(29.9);
    
    //logger and random seed
    private SeededRandom rng; 
    private Logger log;
    
    //boolean to track start of turn 
    private bool _startOfTurn = true;
    
    //counters to track stats for a single game
    private int _simsCounter; // total number of MCTS simulations for a single game 
    private int _turnCounter; // total number of turns for a single game
    private int _moveCounter; // total number of moves for a single gam
    
    //variables ot track stats for a single move
    private static int _depthCounter ; //total final tree depth prior to choosing move
    private static List<int> _widthTreeLayers; //number of nodes in each layer of tree when choosing move (which are assuming we
    //dont go more than five levels deep)
    private static int _moveTimeOutCounter; //number of times we time out a move across a game
    
    //counters across multiple games
    private static int _gameCounter; // total number of games played in a session
    private static int _totalSimsCounter; //tracks total number of sims across all games played in a session
    
    //parameters for MCTS
    private readonly double K = Math.Sqrt(2); 
    
    //Taken from BestMCTS3 - as we reuse the same heuristic
    private GameStrategy _strategy = new(10, GamePhase.EarlyGame);
    
    //Timer for each game
    private TimeSpan _totalTimeForGame;
    
    private void PrepareForGame()
    { 
        //if any agent set-up needed it can be done here
        
        //seed random number generator
        long seed = DateTime.Now.Ticks;
        rng = new(123);  
        //rng = new((ulong)seed); 
        
        //create logger object
        log = new Logger();
        log.P1LoggerEnabled = true;
        log.P2LoggerEnabled = true;
        
        //initialise start of turn and game bools
        _startOfTurn = true;
        
        //initialise counters
        _turnCounter = 0;
        _moveCounter = 0;
        _simsCounter = 0;
        _depthCounter = 0;
        _widthTreeLayers = Enumerable.Repeat(0, 15).ToList(); 
        _moveTimeOutCounter = 0;
        
        //increment game counter
        _gameCounter += 1;
        
        //initialise timer for this game
        _totalTimeForGame = TimeSpan.FromSeconds(0);
    }

    public SOISMCTS()
    {
        this.PrepareForGame();
    }
    
    public override PatronId SelectPatron(List<PatronId> availablePatrons, int round)
    {
        return availablePatrons.PickRandom(rng);
    }

    public override Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
    {
        //timer for this move
        Stopwatch moveTimer = new Stopwatch();
        moveTimer.Start();
        
        if (_startOfTurn)
        {
            _startOfTurn = false;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
            SelectStrategy(gameState);
        }
        
        //if only possible move is end turn then just end the turn
        if (possibleMoves.Count == 1 && possibleMoves[0].Command == CommandEnum.END_TURN)
        {
            _startOfTurn = true;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
            _turnCounter += 1;
            _moveCounter += 1;
            _widthTreeLayers[0] += 1;
            return possibleMoves[0];
        }
        
        //Initialise a root node
        SeededGameState s = gameState.ToSeededGameState((ulong) rng.Next());
        List<Move> filteredMoves = FilterMoves(possibleMoves, s);
        Determinisation d = new Determinisation(s, filteredMoves); //not possible moves are compatible with all seeds at the root
        InfosetNode root = new InfosetNode(null, null, d);
        
        Move chosenMove = null;
        if (_usedTimeInTurn + _timeForMoveComputation >= _turnTimeout)
        {
            _moveTimeOutCounter += 1;
            chosenMove = possibleMoves.PickRandom(rng);
        }
        else
        {
            int maxDepthForThisMove = 0;
            //Stopwatch timer = new Stopwatch();
            //timer.Start();
            //while (timer.Elapsed < _timeForMoveComputation)
            int maxIterations = 2500;
            for(int i = 0; i < maxIterations; i++)
            {
                //in InfosetMCTS each iteration of the loop starts with a new determinisation, which we use to explore the same tree
                //updating the stats for each tree node
                // s = gameState.ToSeededGameState((ulong) rng.Next());
                // filteredMoves = FilterMoves(possibleMoves, s);
                // d = new Determinisation(s, filteredMoves); 
                // //and set as determinisation to use for this iteration
                // root.SetCurrentDeterminisationAndMoveHistory(d, null);   
                
                //enter selection routine - return an array of nodes with index zero corresponding to root and final
                //entry corresponding to the node selected for expansion
                var(pathThroughTree, cvd, uvd) = select(root);

                //if selected node has moves leading to nodes not in the tree then expand the tree
                InfosetNode selectedNode = pathThroughTree[pathThroughTree.Count -1];
                //List<Move> uvd = selectedNode.GetMovesWithNoChildren();
                InfosetNode expandedNode = selectedNode;
                
                //dont expand an end_turn node
                if (!selectedNode._endTurn)
                {
                    if (uvd.Count != 0)
                    {
                        expandedNode = Expand(selectedNode, uvd, pathThroughTree);
                    }
                    else
                    {
                        //this should never happen unless we are in a game end state
                    }
                }

                //next we simulate our playouts from our expanded node 
                double payoutFromExpandedNode = Simulate(expandedNode);

                //next we complete the backpropagation step
                BackPropagation(payoutFromExpandedNode, pathThroughTree);

                _simsCounter += 1;
                _totalSimsCounter += 1;

                maxDepthForThisMove = Math.Max(maxDepthForThisMove, pathThroughTree.Count);
            }
            _usedTimeInTurn += _timeForMoveComputation;
            
            //increase depth counter
            _depthCounter += maxDepthForThisMove;
            
            //increase width counter
            Dictionary<int,int> treeWidthForEachLayerForThisMove = InfosetNode.CalculateLayerSpans(root);
            for (int i = 0; i < maxDepthForThisMove; i++)
            {
                if (treeWidthForEachLayerForThisMove[0] != 1)
                {
                    int j = 0;
                }
                _widthTreeLayers[i] += treeWidthForEachLayerForThisMove[i];
            }

            //finally we return the move from the root node that leads to a node with the maximum visit count
            chosenMove = chooseBestMove(root);
        }
        
        if (chosenMove.Command == CommandEnum.END_TURN)
        {
            _startOfTurn = true;
            _turnCounter += 1;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
        }

        moveTimer.Stop();
        _totalTimeForGame += moveTimer.Elapsed;
        _moveCounter += 1;
        return chosenMove;
    }
    
    //returns the selected path through the tree, and also the children in tree and move not in tree for the final selected node
    public (List<InfosetNode>, HashSet<InfosetNode>, List<Move>) select(InfosetNode startNode)
    { 
        //descend our infoset tree (restricted to nodes/actions compatible with the current determinisation of our start node)
        //Each successive node is chosen based on the UCB score, until a node is reached such that all moves from that node are not in the tree
        //or that node is terminal
        InfosetNode bestNode = startNode;
        var (cvd, uvd) = startNode.calcChildrenInTreeAndMovesNotInTree();
        //this contains each node passed through in this iteration 
        List<InfosetNode> pathThroughTree = new List<InfosetNode>();
        pathThroughTree.Add(startNode);
        while (bestNode.GetCurrentDeterminisation().GetState().GameEndState == null && uvd.Count == 0)
        {
            double bestVal = 0;
            foreach(InfosetNode node in cvd)
            {
                double val = node.UCB(K);
                if (val > bestVal)
                {
                    bestVal = val;
                    bestNode = node;
                }
            }

            (cvd, uvd) = bestNode.calcChildrenInTreeAndMovesNotInTree();
            pathThroughTree.Add(bestNode);

            //dont continue to select past an end_turn node
            if (bestNode._endTurn)
            {
                break;
            }
        }
        return (pathThroughTree, cvd, uvd);
    }
    
    private InfosetNode Expand(InfosetNode selectedNode, List<Move> selectedUVD, List<InfosetNode> pathThroughTree)
    {
        //choose a move at random from our list of moves that do not have nodes in the tree
        //and add child node to tree
        //List<Move> uvd = selectedNode.GetMovesWithNoChildren();
        Move? move = null;
        InfosetNode? newNode = null;
        if (selectedUVD.Count >= 1)
        {
            move = selectedUVD.PickRandom(rng);
            var (newSeededGameState, newMoves) = selectedNode.GetCurrentDeterminisation().GetState().ApplyMove(move);
            List<Move> newFilteredMoves = FilterMoves(newMoves, newSeededGameState); //TODO:Should we be filtering here?
            Determinisation newd = new Determinisation(newSeededGameState, newFilteredMoves);
            newNode = selectedNode.CreateChild(move, newd);
            pathThroughTree.Add(newNode);
            
            //next we add this new node to the parent's list of compatible children for this iteration, and also 
            //remove the move that generated this node from the list of moves that dont have a child in the tree. 
            //This means we dont need to call calcChildrenInTreeAndMovesNotInTree during the back propagation function
            selectedNode._compatibleChildrenInTree.Add(newNode);
            selectedNode._currentMovesWithNoChildren.Remove(move);
        }
        else
        {
            //Here we are trying to expand when all moves from the selected node have children already in the tree. 
            //This shouldn't be possible if our select function is working correctly
            throw new Exception("Error in expansion");
        }
        //return new infosetNode object, corresponding to the selected node
        return newNode;
    }

    //simulate our game from a given determinisation associated with our expanded node (ignoring information sets)
    //adapted from last years winner
    public double Simulate(InfosetNode startNode)
    {
        //if the move from the parent is END_TURN, we need to just take the heuristic value for the parent (end turn
        //doesnt change the value of player's position, and we cant apply the heuristic when the current player is the enemy 
        //player)
        if (startNode._endTurn)
        {
            return _strategy.Heuristic(startNode.Parent.GetCurrentDeterminisation().GetState());
        }
        
        SeededGameState gameState = startNode.GetCurrentDeterminisation().GetState();
        //check that only move from startNode isn't an end turn
        List<Move> possibleMoves = startNode.GetCurrentDeterminisation().GetMoves();
        double finalPayOff = 0;
        List<Move> notEndMoves = possibleMoves.Where(m => m.Command != CommandEnum.END_TURN).ToList();
        if (notEndMoves.Count == 0)
        {
            return _strategy.Heuristic(gameState);
        }
        Move move = notEndMoves.PickRandom(rng);
    
        while (move.Command != CommandEnum.END_TURN)
        {
            (gameState, possibleMoves) = gameState.ApplyMove(move);
            notEndMoves = possibleMoves.Where(m => m.Command != CommandEnum.END_TURN).ToList();
    
            if (notEndMoves.Count > 0)
            {
                move = notEndMoves.PickRandom(rng);
            }
            else
            {
                move = Move.EndTurn();
            }
            
        }
        
        return _strategy.Heuristic(gameState);
    }
    
    //function to backpropagate simulation playout results
    private void BackPropagation(double finalPayout, List<InfosetNode> pathThroughTree)
    {
        //need to traverse tree from the playout start node back up to the root of the tree
        //note that our path through the tree should hold references to tree nodes and hence can be updated directly
        //(program design could be improved here!)
        for(int i = 0; i < pathThroughTree.Count; i++)
        {
            InfosetNode node = pathThroughTree[i];
            node.VisitCount += 1;
            node.TotalReward += finalPayout;
            node.MaxReward = Math.Max(finalPayout, node.MaxReward);
            node.AvailabilityCount += 1;
            //by definition the final node in the path through the tree wont have any compatible children in the tree
            //to see this there are two case, 1. where selected node has uvd.count =0 (and the epxanded node is the same as the selected node
            //, in which case we should be in an end game state and there are no further children to include in the tree
            //or 2. uvd.count is not zero for the sected node, and the expanded node has just been added into the tree in which case
            //again we have no compatible children in the tree
            if (i < (pathThroughTree.Count - 1))
            {
                foreach (InfosetNode child in node._compatibleChildrenInTree)
                {
                    child.AvailabilityCount += 1;
                }
            }
        }
    }
    
    //chooses child from root node with highest visitation number
    public Move chooseBestMove(InfosetNode rootNode)
    {
        //note that for the root node, all possible moves are compatible with any determinisation
        //as it is the observing player's turn to go. Also the move that was used to last to go from the root
        //to the best node would be the same as any other move to go between these nodes 
        // int bestVisitCount = 0;
        // Move bestMove = null;
        // foreach (InfosetNode node in rootNode.Children)
        // {
        //     if (node.VisitCount > bestVisitCount)
        //     {
        //         bestVisitCount = node.VisitCount;
        //         bestMove = node._currentMoveFromParent;
        //     }   
        // }

        double bestScore = 0;
        Move bestMove = null;
        foreach (InfosetNode node in rootNode.Children)
        {
            if (node.MaxReward >= bestScore) //note heuristic can take a value of zero
            {
                bestScore = node.MaxReward;
                bestMove = node.GetCurrentMoveFromParent();
            }   
        }
        
        return bestMove;
    }
    
    //taken from MCTSBot
    private List<Move> NotEndTurnPossibleMoves(List<Move> possibleMoves)
    {
         return possibleMoves.Where(m => m.Command != CommandEnum.END_TURN).ToList();
    }
    
     //taken from BestMCTS3
    private List<Move> FilterMoves(List<Move> moves, SeededGameState gameState)
    {
        moves.Sort(new MoveComparer());
        if (moves.Count == 1) return moves;
        if (gameState.BoardState == BoardState.CHOICE_PENDING)
        {
            List<Move> toReturn = new();
            switch (gameState.PendingChoice!.ChoiceFollowUp)
            {
                case ChoiceFollowUp.COMPLETE_TREASURY:
                    List<Move> gold = new();
                    foreach (Move mv in moves)
                    {
                        var mcm = mv as MakeChoiceMove<UniqueCard>;
                        UniqueCard card = mcm!.Choices[0];
                        if (card.CommonId == CardId.BEWILDERMENT) return new List<Move> { mv };
                        if (card.CommonId == CardId.GOLD && gold.Count == 0) gold.Add(mv);
                        if (card.Cost == 0) toReturn.Add(mv); // moze tez byc card.Type == 'Starter'
                    }
                    if (gold.Count == 1) return gold;
                    if (toReturn.Count > 0) return toReturn;
                    return new List<Move> { moves[0] };
                case ChoiceFollowUp.DESTROY_CARDS:
                    List<(Move, double)> choices = new();
                    foreach (Move mv in moves)
                    {
                        var mcm = mv as MakeChoiceMove<UniqueCard>;
                        if (mcm!.Choices.Count != 1) continue;
                        choices.Add((mv, _strategy.CardEvaluation(mcm!.Choices[0], gameState)));
                    }
                    choices.Sort(new PairOnlySecond());
                    List<CardId> cards = new();
                    for (int i = 0; i < Math.Min(3, choices.Count); i++)
                    {
                        var mcm = choices[i].Item1 as MakeChoiceMove<UniqueCard>;
                        cards.Add(mcm!.Choices[0].CommonId);
                    }
                    foreach (Move mv in moves)
                    {
                        var mcm = mv as MakeChoiceMove<UniqueCard>;
                        bool flag = true;
                        foreach (UniqueCard card in mcm!.Choices)
                        {
                            if (!cards.Contains(card.CommonId))
                            {
                                flag = false;
                                break;
                            }
                        }
                        if (flag) toReturn.Add(mv);
                    }
                    if (toReturn.Count > 0) return toReturn;
                    return moves;
                case ChoiceFollowUp.REFRESH_CARDS: // tu i tak musi byc duzo wierzcholkow i guess
                    List<(Move, double)> possibilities = new();
                    foreach (Move mv in moves)
                    {
                        var mcm = mv as MakeChoiceMove<UniqueCard>;
                        double val = 0;
                        foreach (UniqueCard card in mcm!.Choices)
                        {
                            val += _strategy.CardEvaluation(card, gameState);
                        }
                        possibilities.Add((mv, val));
                    }
                    possibilities.Sort(new PairOnlySecond());
                    possibilities.Reverse();
                    if (gameState.PendingChoice.MaxChoices == 3)
                    {
                        for (int i = 0; i < Math.Min(10, possibilities.Count); i++)
                        {
                            toReturn.Add(possibilities[i].Item1);
                        }
                    }
                    if (gameState.PendingChoice.MaxChoices == 2)
                    {
                        for (int i = 0; i < Math.Min(6, possibilities.Count); i++)
                        {
                            toReturn.Add(possibilities[i].Item1);
                        }
                    }
                    if (gameState.PendingChoice.MaxChoices == 1)
                    {
                        for (int i = 0; i < Math.Min(3, possibilities.Count); i++)
                        {
                            toReturn.Add(possibilities[i].Item1);
                        }
                    }
                    if (toReturn.Count == 0) return moves;
                    return toReturn;
                default:
                    return moves;
            }
        }
        foreach (Move mv in moves)
        {
            if (mv.Command == CommandEnum.PLAY_CARD)
            {
                var mvCopy = mv as SimpleCardMove;
                if (InstantPlayCards.IsInstantPlay(mvCopy!.Card.CommonId))
                {
                    return new List<Move> { mv };
                }
            }
        }
        return moves;
    }
    
    //taken from previous years winner
    void SelectStrategy(GameState gameState)
    {
        var currentPlayer = gameState.CurrentPlayer;
        int cardCount = currentPlayer.Hand.Count + currentPlayer.CooldownPile.Count + currentPlayer.DrawPile.Count;
        int points = gameState.CurrentPlayer.Prestige;
        if (points >= 27 || gameState.EnemyPlayer.Prestige >= 30)
        {
            _strategy = new GameStrategy(cardCount, GamePhase.LateGame);
        }
        else if (points <= 10 && gameState.EnemyPlayer.Prestige <= 13)
        {
            _strategy = new GameStrategy(cardCount, GamePhase.EarlyGame);
        }
        else
        {
            _strategy = new GameStrategy(cardCount, GamePhase.MidGame);
        }
    }
    
    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
        double avgMovesPerTurn = _moveCounter/ (1.0 * _turnCounter);
        double avgSimsPerMove = _simsCounter / (1.0 * _moveCounter);
        double avgDepthPerMove = _depthCounter/ (1.0 * _moveCounter);
        double avgMoveTimeOutsPerTurn = _moveTimeOutCounter/(1.0 * _turnCounter);
        
        string message = "Game count: " + _gameCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Turn Counter: " + _turnCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of moves per turn: " + avgMovesPerTurn.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of move timeouts per turn: " + avgMoveTimeOutsPerTurn.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of simulations per move: " + avgSimsPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average tree depth searched per move: " + avgDepthPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average widths of each layer of the tree per move: ";
        for (int i = 0; i < _widthTreeLayers.Count; i++)
        {
            message += (_widthTreeLayers[i]/ (1.0 * _moveCounter)).ToString() + ",";
        }
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Winner: " + state.Winner.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Game end reason: " + state.Reason.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        int minutes = _totalTimeForGame.Minutes;
        int seconds = _totalTimeForGame.Seconds;
        message = "Time taken by SOISMCTS bot for this game: " + $"Elapsed time: {minutes} minutes and {seconds} seconds.";
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "total number of sims across all games: " + _totalSimsCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        
        //prepare for next game
        this.PrepareForGame();
    }
}

//Each node corresponds to an information set for the observing player for a tree,
//and also contains the determinisation that was used when the node was last visited in the tree 
public class InfosetNode
{
    public InfosetNode? Parent; //parent node
    public HashSet<InfosetNode> Children; //list of all children that have been visited from this node irrespective of the determinisation
    public double TotalReward;
    public double MaxReward;
    public int VisitCount;
    public int AvailabilityCount;
    
    //Next we have a reference move history that keeps it's value between MCTS iterations and changing determinisations,
    //Any nodes with identical reference move histories are ocnsidered ot be the same node
    private List<Move> _refMoveHistory;
    
    //member variables to keep track of current determinisation being used, which children are compatible with the current
    //detereminisation and are in the tree, also the list of moves used to get to this node based on the current determinisation.
    private Determinisation? _currentDeterminisation; //to store determinisation that is currently being used in MCTS
    private List<Move>? _currentMoveHistory; //stores the history of moves from the root to this node, based on the current determinisation (this also
    //includes the current move from parent as the final entry in the list)
    public List<Move>? _currentMovesWithNoChildren; //stores moves from node using current determinisation that have no children
    public HashSet<InfosetNode>? _compatibleChildrenInTree; //list of children of this node compatible with moves using current determinisation 
    
    //to label nodes which have been arrived at through an END_TURN command
    public bool _endTurn;
    
    //hash code based on reference move history that is used to search for the node
    private ulong _hashCode;
    
    public InfosetNode(InfosetNode? parent, Move? currentMoveFromParent, Determinisation d)
    {
        Parent = parent;
        Children = new HashSet<InfosetNode>();
        
        //initialise values for UCB calc
        TotalReward = 0;
        MaxReward = 0;
        VisitCount = 1;
        AvailabilityCount = 1;
        
        //initialise references to define equivalence classes for information sets
        _refMoveHistory = new List<Move>(); // we initialise ref move history using the determinisation being used at the time of the node's creation
        //(form parent). Then in future iterations of the MCTS loop, the reference move history will be used to assess if this node is then equivalent to another node with
        //some other move history based on a different determinisation. We dont take the parents reference move history, as this may have come form a different
        //determinisation than the parent move (would this make a difference?)
        if (parent is not null)
        {
            if (parent._currentMoveHistory is not null)
            {
                foreach (Move mv in parent._currentMoveHistory)
                {
                    _refMoveHistory.Add(mv);
                }
            }

            if (currentMoveFromParent is not null)
            {
                _refMoveHistory.Add(currentMoveFromParent);
            }
        }

        _endTurn = false;
        if ((currentMoveFromParent is not null) && currentMoveFromParent.Command == CommandEnum.END_TURN)
        {
            _endTurn = true;
        }
            
        //set current determinisation and current move history
        SetCurrentDeterminisationAndMoveHistory(d, _refMoveHistory);
        
        //set hashcode
        _hashCode = calcHashCode(_refMoveHistory);
    }

    private ulong calcHashCode(List<Move> moveHistory)
    {
        unchecked // Allow arithmetic overflow, which is fine in this context
        {
            ulong hash = 19;
            foreach (Move mv in moveHistory)
            {
                //note a move should only be null at the root, so the root node will have a hash code of 19
                if (mv is not null)
                {
                    hash = hash * 31 + MoveComparer.HashMove(mv);
                }
            }
            return hash;
        }
    }
    
    public override bool Equals(object obj)
    {
        if (obj == null || obj is not InfosetNode)
            return false;

        InfosetNode other = (InfosetNode) obj;

        return CheckEquivalence(other._refMoveHistory);
    }

    public override int GetHashCode()
    {
        //once reference move history is set when creating node, hash code is fixed.
        return (int)_hashCode;
    }
    
    //this method updates the current determinisation and move history for a node, also
    //for each of the nodes further down the tree sets to null the current determinisation
    //and move history, so that they get recalculated when moving down the tree with the current determinisation
    public void SetCurrentDeterminisationAndMoveHistory(Determinisation? d, List<Move>? currentMoveHistory)
    {
        //set current determinisation
        _currentDeterminisation = d;
        _currentMovesWithNoChildren = null; //will be calculated as needed
        _compatibleChildrenInTree = null; //will be calculated as needed
        if (currentMoveHistory is null)
        {
            _currentMoveHistory = new List<Move>();
        }
        else
        {
            _currentMoveHistory = currentMoveHistory;
        }
        
        //if this node has children then we need to clear the move histories and current determinisations from them,
        //so they can be set and recalculated as needed. This means that there will be children in the tree without
        //any current parent move, however they will still have a reference move history, with a move from the parent node to the child node.
        foreach (InfosetNode child in Children)
        {
            child.SetCurrentDeterminisationAndMoveHistory(null, null);
        }
    }

    public Move GetCurrentMoveFromParent()
    {
        return _currentMoveHistory[_currentMoveHistory.Count - 1];
    }
    
    public Determinisation? GetCurrentDeterminisation()
    {
        return _currentDeterminisation;
    }

    //calculate upper confidence bound for trees, bandit algorithm for MCTS tree policy
    public double UCB(double K)
    {
        double ucbVal = TotalReward / (VisitCount * 1.0) + K * Math.Sqrt(Math.Log(AvailabilityCount) / (VisitCount * 1.0));
        return ucbVal;
    }
    
    //For current determinisation calculates compatible children in the tree
    //and list of moves for which there are no children
    public (HashSet<InfosetNode>, List<Move>) calcChildrenInTreeAndMovesNotInTree()
    {
        // Don't add children of an end turn node into the tree as we are then analyzing enemy player nodes also.
        if (_endTurn)
        {
            _compatibleChildrenInTree = new HashSet<InfosetNode>(); // never include children from an end turn in our tree
            _currentMovesWithNoChildren = null; // doesn't have any meaning when we are at end an end turn
            return (_compatibleChildrenInTree, _currentMovesWithNoChildren);
        }

        _compatibleChildrenInTree = new HashSet<InfosetNode>();
        _currentMovesWithNoChildren = new List<Move>();
        
        foreach (Move move in _currentDeterminisation.GetMoves())
        {
            // Create new state
            var (newState, newMoves) = _currentDeterminisation.GetState().ApplyMove(move);

            // Create move history to newState
            List<Move> moveHistoryToState = new List<Move>(_currentMoveHistory) { move };

            // Find if new state or move history is in tree or not
            bool foundInChildren = false;

            foreach (InfosetNode child in Children)
            {
                if (child.CheckEquivalence(moveHistoryToState))
                {
                    // Found child node that represents an information set containing equivalent states
                    foundInChildren = true;
                    child.SetCurrentDeterminisationAndMoveHistory(new Determinisation(newState, newMoves),
                        moveHistoryToState);

                    if (!_compatibleChildrenInTree.Contains(child))
                    {
                        _compatibleChildrenInTree.Add(child);
                    }

                    break; // No need to check other children if we found a match
                }
            }

            if (!foundInChildren)
            {
                _currentMovesWithNoChildren.Add(move);
            }
        }

        return (_compatibleChildrenInTree, _currentMovesWithNoChildren);
    }
    
    public InfosetNode CreateChild(Move? parentMove, Determinisation newd)
    {
        InfosetNode childNode = new InfosetNode(this, parentMove, newd);
        Children.Add(childNode);
        
        return childNode;
    }
    
    //checks to see if two nodes are equivalent, based on their reference move history
    private bool CheckEquivalence(List<Move>? moveHistory)
    {
        if (!checkMovesListAreEqual(this._refMoveHistory, moveHistory))
            return false;
        
        return true;
    }
    
    //simple function to check that lists of moves are the same
    private bool checkMovesListAreEqual(List<Move> list1, List<Move> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        if (list1.Count == 0)
            return true;

        for (int index = 0; index < list1.Count; index++)
        {
            if (!MoveComparer.AreIsomorphic(list1[index], list2[index]))
                return false;
        }

        return true;
    }
    
    //function to compute width of tree at each level
    public static Dictionary<int, int> CalculateLayerSpans(InfosetNode startNode)
    {
        Dictionary<int, int> layerSpans = new Dictionary<int, int>();
        CalculateLayerSpansRecursive(startNode, 0, layerSpans);
        return layerSpans;
    }

    private static void CalculateLayerSpansRecursive(InfosetNode node, int layer, Dictionary<int, int> layerSpans)
    {
        if (node == null) return;

        // Increment the count of nodes at this layer
        if (layerSpans.ContainsKey(layer))
        {
            layerSpans[layer]++;
        }
        else
        {
            layerSpans[layer] = 1;
        }

        // Recur for each child
        foreach (var child in node.Children)
        {
            CalculateLayerSpansRecursive(child, layer + 1, layerSpans);
        }
    }
}

//struct to encapsulate a specific determinisation, which includes a concrete game state and compatible moves
public class Determinisation
{
    private SeededGameState? state;
    private List<Move>? moves;

    public Determinisation(SeededGameState? gamestate, List<Move>? compatibleMoves)
    {
        state = gamestate;
        moves = compatibleMoves;
    }

    public SeededGameState? GetState()
    {
        return state;
    }
    
    public List<Move>? GetMoves()
    {
        return moves;
    }
}


