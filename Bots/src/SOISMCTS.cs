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
        _totalTimeForGame += moveTimer.Elapsed();
        _moveCounter += 1;
        return chosenMove;
    }
    
    //returns the selected path through the tree, and also the children in tree and move not in tree for the final selected node
    public (List<InfosetNode>, List<InfosetNode>, List<Move>) select(InfosetNode startNode)
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
    public List<InfosetNode> Children; //list of all children that have been visited from this node irrespective of the determinisation
    public double TotalReward;
    public double MaxReward;
    public int VisitCount;
    public int AvailabilityCount;
    
    //Next we have a reference determinisation and a reference move history that keep their value between MCTS iterations and changing determinisations,
    //these are used to compare against to see if we have equivalent nodes (null values are used here when we set up a root node)
    private Determinisation _refDeterminisation;
    private List<Move> _refMoveHistory;
    
    //member variables to keep track of current determinisation being used, which children are compatible with the current
    //detereminisation and are in the tree, also the list of moves used to get to this node based on the current determinisation.
    private Determinisation? _currentDeterminisation; //to store determinisation that is currently being used in MCTS
    private List<Move>? _currentMoveHistory; //stores the history of moves from the root to this node, based on the current determinisation (this also
    //includes the current move from parent as the final entry in the list)
    public List<Move>? _currentMovesWithNoChildren; //stores moves from node using current determinisation that have no children
    public List<InfosetNode>? _compatibleChildrenInTree; //list of children of this node compatible with moves using current determinisation 
    
    //to label nodes which have been arrived at through an END_TURN command
    public bool _endTurn;
    
    public InfosetNode(InfosetNode? parent, Move? currentMoveFromParent, Determinisation d)
    {
        Parent = parent;
        Children = new List<InfosetNode>();
        
        //initialise values for UCB calc
        TotalReward = 0;
        MaxReward = 0;
        VisitCount = 1;
        AvailabilityCount = 1;
        
        //initialise references to define equivalence classes for information sets
        _refDeterminisation = d;
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
    
    // //For current determinisation calculates compatible children in the tree
    // //and list of moves for which there are no children
    // public (List<InfosetNode>, List<Move>) calcChildrenInTreeAndMovesNotInTree()
    // {
    //     // Don't add children of an end turn node into the tree as we are then analyzing enemy player nodes also.
    //     if (_endTurn)
    //     {
    //         _compatibleChildrenInTree = new List<InfosetNode>(); // never include children from an end turn in our tree
    //         _currentMovesWithNoChildren = null; // doesn't have any meaning when we are at end an end turn
    //         return (_compatibleChildrenInTree, _currentMovesWithNoChildren);
    //     }
    //     
    //     _compatibleChildrenInTree = new List<InfosetNode>();
    //     _currentMovesWithNoChildren = new List<Move>();
    //
    //     // Use a HashSet to quickly check if a child is already in _compatibleChildrenInTree
    //     var compatibleChildrenSet = new HashSet<InfosetNode>(_compatibleChildrenInTree);
    //
    //     foreach (Move move in _currentDeterminisation.GetMoves())
    //     {
    //         // Create new state
    //         var (newState, newMoves) = _currentDeterminisation.GetState().ApplyMove(move);
    //
    //         // Create move history to newState
    //         var moveHistoryToState = new List<Move>(_currentMoveHistory) { move };
    //         
    //         // Find if new state or move history is in tree or not
    //         bool foundInChildren = false;
    //
    //         foreach (InfosetNode child in Children)
    //         {
    //             if (child.CheckEquivalence(newState, moveHistoryToState))
    //             {
    //                 // Found child node that represents an information set containing equivalent states
    //                 foundInChildren = true;
    //                 child.SetCurrentDeterminisationAndMoveHistory(new Determinisation(newState, newMoves), moveHistoryToState);
    //                 
    //                 if (!compatibleChildrenSet.Contains(child))
    //                 {
    //                     _compatibleChildrenInTree.Add(child);
    //                     compatibleChildrenSet.Add(child);
    //                 }
    //                 break; // No need to check other children if we found a match
    //             }
    //         }
    //
    //         if (!foundInChildren)
    //         {
    //             _currentMovesWithNoChildren.Add(move);
    //         }
    //     }
    //     
    //     return (_compatibleChildrenInTree, _currentMovesWithNoChildren);
    // }
    
    //For current determinisation calculates compatible children in the tree
    //and list of moves for which there are no children
    //TODO: need to figure out how to optimise this, it is very inefficient with three nested loops!
    //but lets get this working first.....
    public (List<InfosetNode>, List<Move>) calcChildrenInTreeAndMovesNotInTreeOld()
    {
        //dont add children of an end turn node into the tree as we are then analyzing enemy player nodes also.
        if (_endTurn)
        {
            _compatibleChildrenInTree = new List<InfosetNode>(); //never include children from an end_turn in our tree
            _currentMovesWithNoChildren = null; //doesnt have any meaning when we are at end an end turn
            return (_compatibleChildrenInTree, _currentMovesWithNoChildren);
        }
        
        _compatibleChildrenInTree = new List<InfosetNode>();
        _currentMovesWithNoChildren = new List<Move>();
        foreach (Move move in _currentDeterminisation.GetMoves())
        {
            //create new state
            var (newState, newMoves) = _currentDeterminisation.GetState().ApplyMove(move);
            
            //create move history to newState
            List<Move> moveHistoryToState = new List<Move>();
            _currentMoveHistory.ForEach((moveItem)=>
            {
                moveHistoryToState.Add(moveItem);
            });    
            moveHistoryToState.Add(move);
            
            //find if new state or move history is in tree or not
            bool foundInChildren = false;
            bool foundInCompatibleChildren = false;
            foreach (InfosetNode child in Children)
            {
                if (child.CheckEquivalence(newState, moveHistoryToState))
                {
                    //found child node that represents an information set containing equivalent states
                    foundInChildren = true;
                    child.SetCurrentDeterminisationAndMoveHistory(new Determinisation(newState, newMoves), moveHistoryToState);
                    //check that child isnt already in our list of compatible children
                    foreach (InfosetNode compatibleChild in _compatibleChildrenInTree)
                    {
                        if (compatibleChild.CheckEquivalence(newState, moveHistoryToState))
                        {
                            foundInCompatibleChildren = true;
                            break;
                        }
                    }
    
                    if (!foundInCompatibleChildren)
                    {
                        _compatibleChildrenInTree.Add(child);
                    }
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
        
        //dont think this should be here....too confusing!
        // also need to add to list of compatible children as this function is called when expanding the tree 
        // and hence this child node is be definition compatible with it's parent
        // this._compatibleChildrenInTree.Add(childNode);
        //
        // also need to remove this parent move from the list of moves not in the tree
        // this._currentMovesWithNoChildren.Remove(parentMove);

        return childNode;
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
    
    //checks to see if two nodes are equivalent (currently based on either a reference state or a reference move history)
    private bool CheckEquivalence(SeededGameState state, List<Move>? moveHistory)
    {
        //to check whether our states are equivalent we need to identify information visible to the current player
        //and ensure that is the same in both cases.
        //if (!sameVisibleInfo(state))
        //    return false;

        // if (!simpleVisibleInfo(state)) //12% win rate
        //      return false;
        
        //open loop MCTS - to implement this we check all actions from root node to current state and see if they are the same
        if (!openLoopMCTS(moveHistory))
            return false;
        
        return true;
    }
    
    private bool openLoopMCTS(List<Move>? moveHistory)
    {
        //need to check that all moves are the same for the current player (which we also assume is the observing player)
        if (!checkMovesListAreEqual(this._refMoveHistory, moveHistory))
             return false;

        // if (!checkMovesListAreEqual(this._moveHistory, moveHistory))
        //     return false;
        
        return true;
    }
    
    //TODO::This function eeds updating as we know have nodes in our tree that have the current player as player 2,
    //stemming from aparent move which is end_turn. The function below does not deal with this correctly.
    private bool sameVisibleInfo(SeededGameState state)
    {
        //to check the information visible to the current player is the same between two game states, we need to check the following:
        //1. Information that only the current player can see must be the same - i.e. their hand, and known upcoming draws
        //2. Everything else that can be seen by both observing and non-observing player (i.e. information that is always
        //visible to both players) must be the same. This consists of the following game elements:
        //i) Coins, power and prestige amounts
        //ii) Available Tavern cards on board
        //iii) Status of patrons
        //iv) Played cards are the same (i.e. played pile)
        //v) current player and enemy players are the same
        //vi) Agents on the board are the same
        //vii) Both players will be aware of the others cooldown pile (as they will have seen the cards being played)
        //viii) Any upcoming effects or decisions should be the same
        //iX) Any patron calls and the game end state

        SeededGameState refState = _refDeterminisation.GetState();
        
        //check that the hand of the current player is the same 
        //Cant use the Equals function defined in the UniqueCard class here since it use the UniqueID,
        //so if we have two gold cards which have different IDs this equals function would return false,
        //whereaas here we just need to ensure the card types are the same (which is determined from the CommonID field)
        if (!checkCardsEqualUpToPermutation(refState.CurrentPlayer.Hand, state.CurrentPlayer.Hand))
            return false;
        
        //check that known upcoming draws for the current player are the same (some cards have effects that let a player
        //know what is about to be drawn)
        if (!checkCardsEqualUpToPermutation(refState.CurrentPlayer.KnownUpcomingDraws, state.CurrentPlayer.KnownUpcomingDraws))
            return false;
        
        //////////Now check information visible to both observing and non-observing players/////////////
        
        //check board state is the same (TODO::what does this do?)
        //if (!state.BoardState.Equals(_refState.BoardState))
        //    return false;
        
        //check coins are the same in both states for both players
        if (refState.CurrentPlayer.Coins != state.CurrentPlayer.Coins |
            refState.EnemyPlayer.Coins != state.EnemyPlayer.Coins)
            return false;
        
        //check prestige is the same in both states for both players
        if (refState.CurrentPlayer.Power != state.CurrentPlayer.Power |
            refState.EnemyPlayer.Power != state.EnemyPlayer.Power)
            return false;
        
        //check power is the same in both states for both players
        if (refState.CurrentPlayer.Power != state.CurrentPlayer.Power |
            refState.EnemyPlayer.Power != state.EnemyPlayer.Power)
            return false;
        
        //check identical patron status. So for the list of patron for this games we check that the favour status for
        //each patron is the same between our states
        foreach(PatronId id in state.Patrons)
        {
            if (!(refState.PatronStates.All[id].Equals(state.PatronStates.All[id])))
                return false;
        }
    
        //check tavern cards on board are the same 
        if (!checkCardsEqualUpToPermutation(refState.TavernAvailableCards, state.TavernAvailableCards))
            return false;
        
        //check played cards are the same (TODO::do these need to be in the same order because of combo effects)? 
        if (!checkCardsEqual(refState.CurrentPlayer.Played, state.CurrentPlayer.Played))
            return false;
        
        //check agents on the board are the same for both players . Unfortunately the agent objects have
        //not had their Equals operation over-ridden and we cant use the default operator as it just check memory address
        //and we will want to say two instances of the same agent are the same (if they belong to the same player)
        //so we use a specific function for this check
        if (!(BespokePermutationsCheck<SerializedAgent>(refState.CurrentPlayer.Agents, state.CurrentPlayer.Agents) 
              && BespokePermutationsCheck<SerializedAgent>(refState.EnemyPlayer.Agents, state.EnemyPlayer.Agents)))
            return false;
        
        //The cooldown pile both is visible for both players, as they can observe what has been played. We dont check for enemy player
        //though as we are simulating for ounly the current player
        if (!checkCardsEqualUpToPermutation(refState.CurrentPlayer.CooldownPile, state.CurrentPlayer.CooldownPile))
            return false;

        //other final items to check, that are based on decisions the current game state is pending
        if(!(BespokeEqualsCheck<UniqueBaseEffect>(refState.UpcomingEffects, state.UpcomingEffects)))
            return false;
        
        if(!(BespokeEqualsCheck<UniqueBaseEffect>(refState.StartOfNextTurnEffects, state.StartOfNextTurnEffects)))
            return false;

        //check pending choices (not sure we need this if all played cards are the same)
        if (refState.PendingChoice != null && state.PendingChoice != null)
        {
            if (refState.PendingChoice.Type != state.PendingChoice.Type)
                return false;

            if (state.PendingChoice.Type == Choice.DataType.EFFECT)
            {
                if (!BespokeEqualsCheck<UniqueEffect>(refState.PendingChoice.PossibleEffects,
                        state.PendingChoice.PossibleEffects))
                    return false;
            }
        }
        else
        {
            if (refState.PendingChoice == null && state.PendingChoice != null)
            {
                return false;
            }
            else if (refState.PendingChoice != null && state.PendingChoice == null)
            {
                return false;
            }
        }
        
        //check end game state. 
        if (!EqualsGameEndState(refState.GameEndState, state.GameEndState))
            return false;
        
        //TODO: what is a patron call?
        if (refState.CurrentPlayer.PatronCalls != state.CurrentPlayer.PatronCalls)
            return false;
        
        //items we dont check include CompletedActions, as this holds the complete history of the game, and is not needed.
        //we also dont check combostates, as presumably that is covered by played cards?
        
        //if we survive all our tests return true
        return true;
    }
    
    //simple function to check that moves are the same up to a permutation
    private bool checkMovesEqualUpToPermutation(List<Move> list1, List<Move> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        if (list1.Count == 0)
            return true;

        List<ulong> list1HashCodes = list1.Select(MoveComparer.HashMove).ToList();
        List<ulong> list2HashCodes = list2.Select(MoveComparer.HashMove).ToList();
        
        var sortedList1 = list1HashCodes.OrderBy(x => x).ToList();
        var sortedList2 = list2HashCodes.OrderBy(x => x).ToList();

        for (int i = 0; i < sortedList1.Count; i++)
        {
            if (sortedList1[i] != sortedList2[i])
            {
                return false;
            }
        }

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
    
    //simple function to check if lists of unique cards are equal up to a permutation
    private bool checkCardsEqualUpToPermutation(List<UniqueCard> list1, List<UniqueCard> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        if (list1.Count == 0)
            return true;
        
        var sortedList1 = list1.OrderBy(x => x.CommonId).ToList();
        var sortedList2 = list2.OrderBy(x => x.CommonId).ToList();

        for (int i = 0; i < sortedList1.Count; i++)
        {
            if (sortedList1[i].CommonId != sortedList2[i].CommonId)
            {
                return false;
            }
        }

        return true;
    }
    
    //function to check a list of cards are equal
    private bool checkCardsEqual(List<UniqueCard> list1, List<UniqueCard> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        if (list1.Count == 0)
            return true;
        
        for (int i = 0; i < list1.Count; i++)
        {
            if (list1[i].CommonId != list2[i].CommonId)
            {
                return false;
            }
        }

        return true;
    }
    
    //move careful permutation check where we are in control of equals funciton
    public bool BespokePermutationsCheck<T>(List<T> list1, List<T> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        List<bool> match = Enumerable.Repeat(false, list1.Count).ToList();
        List<bool> used = Enumerable.Repeat(false, list2.Count).ToList();
        for(int index1 = 0;  index1 < list1.Count; index1++)
        {
            for(int index2 = 0;  index2 < list2.Count; index2++)
            {
                if (used[index2] == false)
                {
                    if (EqualsOverride(list1[index1], list2[index2]))
                    {
                        match[index1] = true;
                        used[index2] = true;
                        break;
                    }
                }
            }
        }
        return (match.All(b => b) && used.All(b => b));
    }
    
    public bool BespokeEqualsCheck<T>(List<T> list1, List<T> list2)
    {
        if (list1.Count != list2.Count)
            return false;

        for (int index = 0; index < list1.Count; index++)
        {
            if (!(EqualsOverride(list1[index], list2[index])))
                return false;
        }
        
        return true;
    }

    //this is probably very slow!
    public bool EqualsOverride<T>(T item1, T item2)
    {
        if ((item1 is SerializedAgent) && (item2 is SerializedAgent))
        {
            return EqualsAgent(item1 as SerializedAgent, item2 as SerializedAgent);
        }
        else if ((item1 is UniqueEffect) && (item2 is UniqueEffect))
        {
            return EqualsUniqueEffect(item1 as UniqueEffect, item2 as UniqueEffect);
        }
        else
        {
            throw new Exception("Invalid use  of EqualsOverride");
        }
    }
    
    //we need function to define equality between Agents, Pending choices and GameEndState as these are not provided by game engine
    //and not over-ridden in their respective classes
    private bool EqualsAgent(SerializedAgent agent1, SerializedAgent agent2)
    {
        return (agent1.RepresentingCard.Equals(agent2.RepresentingCard) && agent1.Activated == agent2.Activated
                                                                        && agent1.CurrentHp == agent2.CurrentHp);
    }

    private bool EqualsUniqueEffect(UniqueEffect effect1, UniqueEffect effect2)
    {
        //this is abit of a lazy test, but should be good enough
        if (effect1.Amount != effect2.Amount && effect1.Type != effect2.Type)
            return false;

        return true;
    }
    
    private bool EqualsGameEndState(EndGameState? state1, EndGameState? state2)
    {
        if (state1 != null && state2 != null)
        {
            if (!(state1.Winner == state2.Winner && state1.Reason == state2.Reason))
                return false;
        }
        else
        {
            if (state1 == null && state2 != null)
            {
                return false;
            }
            else if (state1 != null && state2 == null)
            {
                return false;
            }
        }

        return true;
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


//some checking code that needs to be moved into unit tests
    // private void checking()
    // {
    //          //some testing of equals and comparison functions
    //     InfosetNode rootNodeTest = tree.GetRootDeterminisation();
    //     
    //     //generate a node from the root
    //     Determinisation rootDeter = rootNodeTest.GetDeterminisation();
    //     var (newSeededGameState, newPossibleMoves) = rootDeter.state.ApplyMove(rootDeter.moves[0]);
    //     InfosetNode nextNodeDown = new InfosetNode(new Determinisation(newSeededGameState, newPossibleMoves));
    //     
    //     //add it to the tree
    //     tree.TreeNodes.Add(nextNodeDown);
    //     //search the tree for that node
    //     int index = tree.TreeNodes.IndexOf(nextNodeDown);
    //     
    //     //generate two SeededGameStates from a single GameState, craet two information sets and then check for equivalence
    //     SeededGameState seededSate1 = gameState.ToSeededGameState(10);
    //     SeededGameState seededSate2 = gameState.ToSeededGameState(101);
    //     InfosetNode node1 = new InfosetNode(new Determinisation(seededSate1, possibleMoves));
    //     InfosetNode node2 = new InfosetNode(new Determinisation(seededSate2, possibleMoves));
    //     bool test = node1.CheckEquivalentState(node2.GetDeterminisation().state);
    //     
    //     //check card permutation function is correct
    //     List<UniqueCard> cardList1 = new List<UniqueCard>();
    //     List<UniqueCard> cardList2 = new List<UniqueCard>();
    //     List<UniqueCard> cardList3 = new List<UniqueCard>();
    //     UniqueCard card1 = GlobalCardDatabase.Instance.GetCard(CardId.GOLD);
    //     UniqueCard card2 = GlobalCardDatabase.Instance.GetCard(CardId.TITHE);
    //     UniqueCard card3 = GlobalCardDatabase.Instance.GetCard(CardId.AMBUSH);
    //     UniqueCard card4 = GlobalCardDatabase.Instance.GetCard(CardId.BONFIRE);
    //     cardList1.Add(card1);
    //     cardList1.Add(card2);
    //     cardList1.Add(card3);
    //     cardList2.Add(card3);
    //     cardList2.Add(card2);
    //     cardList2.Add(card1);
    //     cardList3.Add(card4);
    //     cardList3.Add(card2);
    //     cardList3.Add(card1);
    //     bool test1 = InfosetNode.AreListsPermutations<UniqueCard>(cardList1, cardList2);
    //     bool test2 = InfosetNode.AreListsPermutations<UniqueCard>(cardList2, cardList3);
    // //test
    // List<SerializedAgent> list1 = new List<SerializedAgent>();
    // List<SerializedAgent> list2 = new List<SerializedAgent>();
    // UniqueCard card = GlobalCardDatabase.Instance.GetCard(CardId.CLANWITCH);
    // Agent bond1 = new Agent(card);
    // Agent bond2 = new Agent(card);
    // SerializedAgent agent1 = new SerializedAgent(bond1);
    // SerializedAgent agent2 = new SerializedAgent(bond2);
    // list1.Add(agent1);
    // list2.Add(agent2);
    // bool test1 = InfosetNode.AreListsPermutations<SerializedAgent>(list1, list2);
    // bool test4 = InfosetNode.AreAgentListsPermutations(list1, list2);
    // }


    // PlayerEnum observingPlayerTest = gameState.CurrentPlayer.PlayerID;
    // //some tests
    // SeededGameState s1 = gameState.ToSeededGameState((ulong) rng.Next());
    // SeededGameState s2 = gameState.ToSeededGameState((ulong) rng.Next());
    // SeededGameState s3 = gameState.ToSeededGameState((ulong) rng.Next());
    // Determinisation d1 = new Determinisation(s1, possibleMoves);
    // InfosetNode node1 = new InfosetNode(null, null, d1, observingPlayerTest);
    // bool test1 = node1.CheckEquivalentState(s2);
    // bool test2 = node1.CheckEquivalentState(s3);
    //
    // var (s4, newMoves1) = s1.ApplyMove(possibleMoves[0]);
    // var (s5, newMoves2) = s2.ApplyMove(possibleMoves[0]);
    // Determinisation d2 = new Determinisation(s4, newMoves1);
    // InfosetNode node2 = new InfosetNode(null, null, d2, observingPlayerTest);
    // bool test4 = node2.CheckEquivalentState(s5);
    //     
    // var (s6, newMoves3) = s2.ApplyMove(possibleMoves[3]);
    // bool test5 = node2.CheckEquivalentState(s6);




