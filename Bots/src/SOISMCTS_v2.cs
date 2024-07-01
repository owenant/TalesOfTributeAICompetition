using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using ScriptsOfTribute.Board.Cards;
using ScriptsOfTribute.Board.CardAction;
using ScriptsOfTribute.utils;

namespace Bots;

//class to implement single observer information set monte carlo tree search
public class SOISMCTS : AI
{
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private readonly TimeSpan _turnTimeout = TimeSpan.FromSeconds(29.9);
    private bool _startOfTurn = true;
    private bool _startOfGame = true;
    private SeededRandom rng; 
    private Logger log;
    private int _simsCounter; // total number of MCTS simulations for a game
    private int _turnCounter; // total number of turns for a game
    private int _moveCounter; // total number of moves for a game
    private static int _gameCounter = 0; // total number of games played
    private static int _depthCounter = 0; //total tree depth explored across all moves
    private static int _widthFirstLayerCounter = 0; //number of nodes in first layer of tree
    private static int _widthSecondLayerCounter = 0; //number of nodes in second layer of tree
    private static int _moveTimeOutCounter = 0; //number of times we time out a move across a game
    private static int _totalSimsCounter = 0; //tracks total number of sims across all games played in session
    
    //counter for number of times play method is called
    private int playMethodCallCount = 0;
    
    //parameters for MCTS
    private readonly double K = 0.7; //explore vs exploit parameter for tree policy
    private readonly int maxSimulationDepth = 0; //only explore current player, if we change this to one or greater
    //we may need to update UCB and heuritsic to reflect eneemy player turns
    
    private GameStrategy _strategy = new(10, GamePhase.EarlyGame);

    public static bool errorcheck = false;
    
    private void PrepareForGame()
    { 
        //if any agent set-up needed it can be done here
        
        //seed random number generator
        long seed = DateTime.Now.Ticks;
        //rng = new(123);  
        rng = new((ulong)seed); 
        
        //create logger object
        log = new Logger();
        log.P1LoggerEnabled = true;
        log.P2LoggerEnabled = true;
        
        //initialise start of turn and game bools
        _startOfTurn = true;
        _startOfGame = true;
        
        //initialise counters
        _turnCounter = 0;
        _moveCounter = 0;
        _simsCounter = 0;
        _depthCounter = 0;
        _widthFirstLayerCounter = 0; 
        _widthSecondLayerCounter = 0;
        _moveTimeOutCounter = 0;
        
        //increment game counter
        _gameCounter += 1;

        //TODO: can we initialise and store a static tree object so that we can re-use the tree from move to move?
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
        //playMethodCallCount += 1;
        if (_startOfTurn)
        {
            _startOfTurn = false;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
            SelectStrategy(gameState);
        }
        
        Move chosenMove = null;
        //if only possible move is end turn then just end the turn
        if (possibleMoves.Count == 1 && possibleMoves[0].Command == CommandEnum.END_TURN)
        {
            _startOfTurn = true;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
            _turnCounter += 1;
            _moveCounter += 1;
            return possibleMoves[0];
        }
        
        //Initialise a root node
        PlayerEnum observingPlayer = gameState.CurrentPlayer.PlayerID; //observing player for information sets in tree
        SeededGameState s = gameState.ToSeededGameState((ulong) rng.Next());
        List<Move> filteredMoves = FilterMoves(possibleMoves, s); //filter on obvious moves to play
        Determinisation d = new Determinisation(s, filteredMoves); //note that all possible moves are compatible with all seeds at the root
        InfosetNode root = new InfosetNode(null, null, d, observingPlayer);

        if (_usedTimeInTurn + _timeForMoveComputation >= _turnTimeout)
        {
            _moveTimeOutCounter += 1;
            chosenMove = possibleMoves.PickRandom(rng);
        }
        else
        {
            int actionCounter = 0;
            int maxDepthForThisMove = 0;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            while (timer.Elapsed < _timeForMoveComputation)
            //int maxIterations = 50;
            //for(int i = 0; i < maxIterations; i++)
            {
                //in InfosetMCTS each iteration of the loop starts with a new determinisation we use to explore the same tree
                //updating the stats for each tree node (which are information sets as seen by a chosen observer, in our
                //case our bot)
                s = gameState.ToSeededGameState((ulong) rng.Next());
                filteredMoves = FilterMoves(possibleMoves, s);
                d = new Determinisation(s, filteredMoves); //not possible moves are compatible with all seeds at the root
                //and set as determinisation to use for this iteration
                root.SetDeterminisationAndParentMove(d, null, null);
                
                //enter selection routine - return an array of nodes with index zero corresponding to root and final
                //entry corresponding to the node selected for expansio
                List<InfosetNode> pathThroughTree = select(root);

                //if selected node has moves leading to nodes not in the tree then expand the tree
                InfosetNode selectedNode = pathThroughTree[pathThroughTree.Count -1];
                List<Move> uvd = selectedNode.GetMovesWithNoChildren();
                InfosetNode expandedNode = selectedNode;
                if (uvd.Count != 0)
                {
                    expandedNode = Expand(selectedNode);
                    pathThroughTree.Add(expandedNode);
                }

                //next we simulate our playouts from our expanded node 
                double payoutFromExpandedNode = Simulate(expandedNode);

                //next we complete the backpropagation step
                BackPropagation(payoutFromExpandedNode, pathThroughTree);

                _simsCounter += 1;
                _totalSimsCounter += 1;
                //noIterations += 1;

                maxDepthForThisMove = Math.Max(maxDepthForThisMove, pathThroughTree.Count);
            }
            _usedTimeInTurn += _timeForMoveComputation;
            
            //increase depth counter
            _depthCounter += maxDepthForThisMove;
            
            //increase width counter
            _widthFirstLayerCounter += root.Children.Count;

            //finally we return the move from the root node that leads to a node with the maximum visit count
            chosenMove = chooseBestMove(root);
            
        }
        
        if (chosenMove.Command == CommandEnum.END_TURN)
        {
            _startOfTurn = true;
            _turnCounter += 1;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
        }
        
        _moveCounter += 1;
        return chosenMove;
    }
    
    //returns the selected path through the tree
    public List<InfosetNode> select(InfosetNode startNode)
    { 
        //descend our infoset tree (restricted to nodes/actions compatible with the current determinisation of our start node)
        //Each successive node is chosen using our tree policy until a node is reached such that all moves from that node are not in the tree
        //or that node is terminal
        InfosetNode bestNode = startNode;
        List<InfosetNode> cvd = startNode.GetCompatibleChildrenInTree();
        List<Move> uvd = startNode.GetMovesWithNoChildren();
        //this contains each node passed through in this iteration 
        List<InfosetNode> pathThroughTree = new List<InfosetNode>();
        pathThroughTree.Add(startNode);
        //change loop for now so that we only go as far as the current players end of turn
        while (bestNode.GetDeterminisation().GetState().GameEndState == null && uvd.Count == 0)
        //while(uvd.Count == 0)
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
            cvd = bestNode.GetCompatibleChildrenInTree();
            uvd = bestNode.GetMovesWithNoChildren();
            pathThroughTree.Add(bestNode);
        }
        return pathThroughTree;
    }

    //uvd is the set of actions from 
    private InfosetNode Expand(InfosetNode selectedNode)
    {
        //choose a move at random from our list of moves that do not have nodes in the tree
        //and add child node to tree
        List<Move> uvd = selectedNode.GetMovesWithNoChildren();
        //note we only want nodes in our tree corresponding to the current player (we do not simulate into enemy player turn)
        //therefore we only take moves that do not include an end_turn.
        List<Move> uvd_no_end_turn = NotEndTurnPossibleMoves(uvd);
        Move? move = null;
        InfosetNode? newNode = null;
        if (uvd_no_end_turn.Count >= 1)
        {
            move = uvd_no_end_turn.PickRandom(rng);
            var (newSeededGameState, newMoves) = selectedNode.GetDeterminisation().GetState().ApplyMove(move);
            Determinisation newd = new Determinisation(newSeededGameState, newMoves);
            newNode = selectedNode.CreateChild(move, newd);
        }
        else
        {
            //if the only way to expand is to do an end_turn and move to a node for the enemy player
            //then we dont expand (we dont include enemy player nodes in our tree)
            newNode = selectedNode;
        }
        //return new infosetNode object, corresponding to the selected node
        return newNode;
    }

    //simulate our game from a given determinisation (ignoring information sets)
    //adapted form last years winner
    public double Simulate(InfosetNode startNode)
    {
        SeededGameState gameState = startNode.GetDeterminisation().GetState();
        
        //List<Move> possibleMoves = node.children.ConvertAll<Move>(m => m.Item2);
        //check that only move from startNode isn't an end turn
        List<Move> possibleMoves = startNode.GetDeterminisation().GetMoves();
        double finalPayOff = 0;
        List<Move> notEndMoves = possibleMoves.Where(m => m.Command != CommandEnum.END_TURN).ToList();
        if (notEndMoves.Count == 0)
        {
            finalPayOff = _strategy.Heuristic(gameState);
            if (finalPayOff < 0.0001)
            {
                int i = 0;
            }

            return finalPayOff;
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
        
        finalPayOff = _strategy.Heuristic(gameState);

        return finalPayOff;
    
        return _strategy.Heuristic(gameState);
    }
    
    //function too backpropagate simulation playout results
    private void BackPropagation(double finalPayout, List<InfosetNode> pathThroughTree)
    {
        //need to traverse tree from the playout start node back up to the root of the tree
        //note that our path through the tree should hold references to tree nodes and hence can be updated directly
        //(program design could be improved here!)
        foreach (InfosetNode node in pathThroughTree)
        {
            node.VisitCount += 1;
            node.TotalReward += finalPayout;
            node.MaxReward = Math.Max(finalPayout, node.MaxReward);
            node.AvailabilityCount += 1;
            List<InfosetNode> cvd = node.GetCompatibleChildrenInTree();
            foreach (InfosetNode child in cvd)
            {
                child.AvailabilityCount += 1;
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
            if (node.MaxReward >= bestScore) //note heuritsic can take a value of zero
            //if (node.MaxReward > bestScore)
            {
                bestScore = node.MaxReward;
                bestMove = node._currentMoveFromParent;
            }   
        }
        
        if (bestMove.Command == CommandEnum.END_TURN)
        {
            int i = 0;
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
        double avgWidthFirstLayerPerMove = _widthFirstLayerCounter/ (1.0 * _moveCounter);
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
        message = "Average width of first layer of tree for each move: " + avgWidthFirstLayerPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Winner: " + state.Winner.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Game end reason: " + state.Reason.ToString();
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
    public InfosetNode? Parent;
    public List<InfosetNode> Children; //list of all children that have been visited from this node
    private SeededGameState _refState; //reference state, i.e. one instance of the set of equivalent states for this node
    private Determinisation? _currentDeterminisation; //to store determinisation that is currently being used in MCTS
    public Move? _currentMoveFromParent; //stores move used to reach this node using determinisation of the parent node
    private List<Move>? _currentMovesWithNoChildren; //stores moves from current determinsation that have no children
    private List<InfosetNode>? _compatibleChildrenInTree; //list of children compatible with current determinisation that are in the list of children
    public double TotalReward;
    public double MaxReward;
    public int VisitCount;
    public int AvailabilityCount;
    public PlayerEnum ObservingPlayer; //observing player is assumed to be the player to play next at the root node
    private List<Move>? _moveHistory; //stores the history of moves from the root to this node, based on the current determinisation (this also
    //includes the current move from parent)
    
    //create a node using a reference determinisation for information set it encapsulates
    public InfosetNode(InfosetNode? parent, Move? moveFromParent, Determinisation d, PlayerEnum observPlayer)
    {
        //when a node is first instatntiated a reference state is stored to define in the information set equivalence class
        _refState = d.GetState();
        
        //pbserving player should be constant across the whole tree, this is enforced by the create child method
        ObservingPlayer = observPlayer;
        
        //initialise values for UCB calc
        TotalReward = 0.0001;
        VisitCount = 1;
        AvailabilityCount = 1;

        Parent = parent;
        Children = new List<InfosetNode>();

        List<Move> parentMoveHistory = new List<Move>();
        if (parent is not null)
        {
            if (parent._moveHistory is not null)
            {
                parentMoveHistory = parent._moveHistory;
            }
        }
        
        this.SetDeterminisationAndParentMove(d, moveFromParent, parentMoveHistory);
    }

    //this method updates the current determinisation and parent move for a node, and then 
    //for each of the nodes furtehr down the tree sets to null the current determinisation
    //and parent move, so that they get recalculated when calling GetChildrenInTreeAndMovesNotInTree
    public void SetDeterminisationAndParentMove(Determinisation? d, Move?  moveFromParent, List<Move>? parentMoveHistory)
    {
        _currentDeterminisation = d;
        _currentMoveFromParent = moveFromParent;
        
        _moveHistory = new List<Move>();
        if (parentMoveHistory is not null)
        {
            parentMoveHistory.ForEach((move)=>
            {
                _moveHistory.Add(move);
            });    
        }
        if (moveFromParent is not null)
        {
            _moveHistory.Add(moveFromParent);
        }
        
        _currentMovesWithNoChildren = null;
        _compatibleChildrenInTree = null;
    }

    public Determinisation? GetDeterminisation()
    {
        return _currentDeterminisation;
    }

    //calculate upper confidence bound for trees, bandit algorithm for MCTS tree policy
    public double UCB(double K)
    {
        double ucbVal = TotalReward / (VisitCount * 1.0) + K * Math.Sqrt(Math.Log(AvailabilityCount) / (VisitCount * 1.0));
        return ucbVal;
    }

    public List<Move> GetMovesWithNoChildren()
    {
        if (_currentMovesWithNoChildren is null)
        {
            calcChildrenInTreeAndMovesNotInTree();
        }
   
        return _currentMovesWithNoChildren;
        
    }

    public List<InfosetNode> GetCompatibleChildrenInTree()
    {
        if (_compatibleChildrenInTree is null)
        {
            calcChildrenInTreeAndMovesNotInTree();
        }
   
        return _compatibleChildrenInTree;
    }
    
    //For current determinisation calculates compatible children in the tree
    //and list of moves for which there are no children
    private void calcChildrenInTreeAndMovesNotInTree()
    {
        _compatibleChildrenInTree = new List<InfosetNode>();
        _currentMovesWithNoChildren = new List<Move>();
        foreach (Move move in _currentDeterminisation.GetMoves())
        {
            var (newState, newMoves) = _currentDeterminisation.GetState().ApplyMove(move);
            
            //create move history to newState
            List<Move> moveHistoryToState = new List<Move>();
            _moveHistory.ForEach((moveItem)=>
            {
                moveHistoryToState.Add(moveItem);
            });    
            moveHistoryToState.Add(move);
            
            //find if new state is in tree or not
            bool found = false;
            foreach (InfosetNode child in Children)
            {
                if (child.CheckEquivalentState(newState, moveHistoryToState))
                {
                    //found child node that represents an information set containing equivalent states
                    found = true;
                    child.SetDeterminisationAndParentMove(new Determinisation(newState, newMoves), move, this._moveHistory);
                    _compatibleChildrenInTree.Add(child);
                    break;
                }
            }

            if (!found)
            {
                _currentMovesWithNoChildren.Add(move);
            }
        }
    }
    
    public InfosetNode CreateChild(Move? parentMove, Determinisation newd)
    {
        //TODO::this needs to add an information set node that is appropriate
        //for the observing player. Dont think this matters, as we are using a seeded game state anyway......
        InfosetNode childNode = new InfosetNode(this, parentMove, newd, this.ObservingPlayer);
        Children.Add(childNode);
        
        //also need to add to list of compatible children as this funciton is called when expanding the tree 
        //and hence this child node is be definition compatible with it's parent
        this._compatibleChildrenInTree.Add(childNode);
        
        //also need to remove this parent move form the list of moves not in the tree
        this._currentMovesWithNoChildren.Remove(parentMove);

        return childNode;
    }
    
    //check if a state is part of the equivalence class for this node
    public bool CheckEquivalentState(SeededGameState state, List<Move>? moveHistoryToState)
    {
        //to check whether our states are equivalent we need to identify information visible to the observing player
        //and ensure that is the same in both cases.
        //if (!sameVisibleInfo(state))
        //    return false;

        // if (!simpleVisibleInfo(state)) //12% win rate
        //      return false;
        
        //open loop MCTS - to implemeht this we check all actions from node to current state and if they are the same
        //up to permutation we say the states are the same
        if (!openLoopMCTS(moveHistoryToState))
            return false;
        
        // //for comparison against standard MCTS
        // if (!(this.EqualsMove(_currentMoveFromParent, parentMove)))
        //     return false;
        
        return true;
    }

    public bool openLoopMCTS(List<Move>? moveHistory)
    {
        //need to check that all moves are the same for the current player (which we also assume is the observing player)
        //we also check that moves are the same up to a permutation.
        if (!checkMovesEqualUpToPermutation(this._moveHistory, moveHistory))
             return false;

        // if (!checkMovesListAreEqual(this._moveHistory, moveHistory))
        //     return false;
        
        return true;
    }
    
     public bool simpleVisibleInfo(SeededGameState state)
    {
        //check current player is the same in both states
        PlayerEnum statePlayerID = state.CurrentPlayer.PlayerID;
        PlayerEnum refStatePlayerID = _refState.CurrentPlayer.PlayerID;
        if (statePlayerID != refStatePlayerID)
            return false;
        
        ////////////check information that is visible only to observing payer//////////////
        SerializedPlayer observingPlayerInRefState;
        if (ObservingPlayer == _refState.CurrentPlayer.PlayerID)
        {
            observingPlayerInRefState = _refState.CurrentPlayer;
        }
        else
        {
            observingPlayerInRefState = _refState.EnemyPlayer;
        }
        
        SerializedPlayer observingPlayerInState;
        if (ObservingPlayer == state.CurrentPlayer.PlayerID)
        {
            observingPlayerInState = state.CurrentPlayer;
        }
        else
        {
            observingPlayerInState = state.EnemyPlayer;
        }
        
        //check that the hand of the observing player is the same 
        //Cant use the Equals function defined in the UniqueCard class here since it use the UniqueID,
        //so if we have two gold cards which have different IDs this equals funciton would return false,
        //wheraas here we just need to ensure the card types are the same 
        if (!checkCardsEqualUpToPermutation(observingPlayerInState.Hand, observingPlayerInRefState.Hand))
            return false;
        
        //check that known upcoming draws for the observing player are the same (some cards have effects that let a player
        //know what is about to be drawn)
        if (!checkCardsEqualUpToPermutation(observingPlayerInState.KnownUpcomingDraws, observingPlayerInRefState.KnownUpcomingDraws))
            return false;
        
        //////////Now check information visible to both observing and non-observing players/////////////
        
        //check board state is the same (TODO::what does this do?)
        //if (!state.BoardState.Equals(_refState.BoardState))
        //    return false;
        
        //check coins are the same in both states for both players
        if (state.CurrentPlayer.Coins != _refState.CurrentPlayer.Coins |
            state.EnemyPlayer.Coins != _refState.EnemyPlayer.Coins)
            return false;
        
        //check prestige is the same in both states for both players
        if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
            state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
            return false;
        
        //check power is the same in both states for both players
        if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
            state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
            return false;
        
        //check identical patron status. So for the list of patron for this games we check that the favour status for
        //each patron is the same between our states
        foreach(PatronId id in state.Patrons)
        {
            if (!(state.PatronStates.All[id].Equals(_refState.PatronStates.All[id])))
                return false;
        }
    
        //check tavern cards on board are the same 
        if (!checkCardsEqualUpToPermutation(state.TavernAvailableCards,_refState.TavernAvailableCards))
            return false;
        
        //check played cards are the same (TODO::do these need to be in the same order)? Might be a good idea to check played
        //cards only for the observing player here, as the detereminisations will cause the enemy player to play different cards
        //on each iteration of the MCTS loop, greatly increasing the branching factor. Also observing players decisions
        //are probably not that affected by what the othe rplayer has done (tales of tribute is probably abit like dominion
        //in that respect
        if (!(BespokeEqualsCheck<UniqueCard>(state.CurrentPlayer.Played,_refState.CurrentPlayer.Played) 
              && BespokeEqualsCheck<UniqueCard>(state.EnemyPlayer.Played,_refState.EnemyPlayer.Played)))
            return false;
        
        //check agents on the board are the same for both players . Unfortunately the agent objects have
        //not had their Equals operation over-ridden and we cant use the default operator as it just check memory address
        //and we will want to say two instances of the same agent are the same (if they belong to the same player)
        //so we use a specific function for this check
        if (!(BespokePermutationsCheck<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
              && BespokePermutationsCheck<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
            return false;
        
        //The cooldown pile both is visible for both players, as they will have seen the cards being played previously
        //Note, this could be an expensive action
        if (!(checkCardsEqualUpToPermutation(state.CurrentPlayer.CooldownPile, _refState.CurrentPlayer.CooldownPile) 
              && checkCardsEqualUpToPermutation(state.EnemyPlayer.CooldownPile,_refState.EnemyPlayer.CooldownPile)))
            return false;
        
        //if we survive all our tests return true
        return true;
    }
    
    public bool sameVisibleInfo(SeededGameState state)
    {
        //to check the information visible to the observing player is the same between two game states, we need to check the following:
        //1. Information that only the observing player can see must be the same - i.e. their hand, and known upcoming draws
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
        
        //check current player is the same in both states
        PlayerEnum statePlayerID = state.CurrentPlayer.PlayerID;
        PlayerEnum refStatePlayerID = _refState.CurrentPlayer.PlayerID;
        if (statePlayerID != refStatePlayerID)
            return false;
        
        ////////////check information that is visible only to observing payer//////////////
        SerializedPlayer observingPlayerInRefState;
        if (ObservingPlayer == _refState.CurrentPlayer.PlayerID)
        {
            observingPlayerInRefState = _refState.CurrentPlayer;
        }
        else
        {
            observingPlayerInRefState = _refState.EnemyPlayer;
        }
        
        SerializedPlayer observingPlayerInState;
        if (ObservingPlayer == state.CurrentPlayer.PlayerID)
        {
            observingPlayerInState = state.CurrentPlayer;
        }
        else
        {
            observingPlayerInState = state.EnemyPlayer;
        }
        
        //check that the hand of the observing player is the same 
        //Cant use the Equals function defined in the UniqueCard class here since it use the UniqueID,
        //so if we have two gold cards which have different IDs this equals funciton would return false,
        //wheraas here we just need to ensure the card types are the same 
        if (!BespokePermutationsCheck<UniqueCard>(observingPlayerInState.Hand, observingPlayerInRefState.Hand))
            return false;
        
        //check that known upcoming draws for the observing player are the same (some cards have effects that let a player
        //know what is about to be drawn)
        if (!BespokePermutationsCheck<UniqueCard>(observingPlayerInState.KnownUpcomingDraws, observingPlayerInRefState.KnownUpcomingDraws))
            return false;
        
        //////////Now check information visible to both observing and non-observing players/////////////
        
        //check board state is the same (TODO::what does this do?)
        //if (!state.BoardState.Equals(_refState.BoardState))
        //    return false;
        
        //check coins are the same in both states for both players
        if (state.CurrentPlayer.Coins != _refState.CurrentPlayer.Coins |
            state.EnemyPlayer.Coins != _refState.EnemyPlayer.Coins)
            return false;
        
        //check prestige is the same in both states for both players
        if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
            state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
            return false;
        
        //check power is the same in both states for both players
        if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
            state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
            return false;
        
        //check identical patron status. So for the list of patron for this games we check that the favour status for
        //each patron is the same between our states
        foreach(PatronId id in state.Patrons)
        {
            if (!(state.PatronStates.All[id].Equals(_refState.PatronStates.All[id])))
                return false;
        }
    
        //check tavern cards on board are the same 
        if (!BespokePermutationsCheck<UniqueCard>(state.TavernAvailableCards,_refState.TavernAvailableCards))
            return false;
        
        //check played cards are the same (TODO::do these need to be in the same order)? Might be a good idea to check played
        //cards only for the observing player here, as the detereminisations will cause the enemy player to play different cards
        //on each iteration of the MCTS loop, greatly increasing the branching factor. Also observing players decisions
        //are probably not that affected by what the othe rplayer has done (tales of tribute is probably abit like dominion
        //in that respect
        if (!(BespokeEqualsCheck<UniqueCard>(state.CurrentPlayer.Played,_refState.CurrentPlayer.Played) 
              && BespokeEqualsCheck<UniqueCard>(state.EnemyPlayer.Played,_refState.EnemyPlayer.Played)))
            return false;
        
        //check agents on the board are the same for both players . Unfortunately the agent objects have
        //not had their Equals operation over-ridden and we cant use the default operator as it just check memory address
        //and we will want to say two instances of the same agent are the same (if they belong to the same player)
        //so we use a specific function for this check
        if (!(BespokePermutationsCheck<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
              && BespokePermutationsCheck<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
            return false;
        
        //The cooldown pile both is visible for both players, as they will have seen the cards being played previously
        if (!(BespokePermutationsCheck<UniqueCard>(state.CurrentPlayer.CooldownPile, _refState.CurrentPlayer.CooldownPile) 
              && BespokePermutationsCheck<UniqueCard>(state.EnemyPlayer.CooldownPile,_refState.EnemyPlayer.CooldownPile)))
            return false;

        //other final items to check, that are based on decisions the current game state is pending
        if(!(BespokeEqualsCheck<UniqueBaseEffect>(state.UpcomingEffects, _refState.UpcomingEffects)))
            return false;
        
        if(!(BespokeEqualsCheck<UniqueBaseEffect>(state.StartOfNextTurnEffects, _refState.StartOfNextTurnEffects)))
            return false;

        //check pending choices (not sure we need this if all played cards are the same)
        if (state.PendingChoice != null && _refState.PendingChoice != null)
        {
            if (state.PendingChoice.Type != _refState.PendingChoice.Type)
                return false;

            if (state.PendingChoice.Type == Choice.DataType.EFFECT)
            {
                if (!BespokeEqualsCheck<UniqueEffect>(state.PendingChoice.PossibleEffects,
                        _refState.PendingChoice.PossibleEffects))
                    return false;
            }
        }
        else
        {
            if (state.PendingChoice == null && _refState.PendingChoice != null)
            {
                return false;
            }
            else if (state.PendingChoice != null && _refState.PendingChoice == null)
            {
                return false;
            }
        }
        
        //check end game state. 
        if (!EqualsGameEndState(state.GameEndState, _refState.GameEndState))
            return false;
        
        //TODO: what is a patron call?
        if (state.CurrentPlayer.PatronCalls != _refState.CurrentPlayer.PatronCalls)
            return false;
        
        //items we dont check include CompletedActions, as this holds the complete history of the game, and is not needed.
        //we also dont check combostates, as presumably that is covered by played cards?
        
        //if we survive all our tests return true
        return true;
    }
    
    //simple function to check that moves are the same up to a permutation
    public bool checkMovesEqualUpToPermutation(List<Move> list1, List<Move> list2)
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
    public bool checkMovesListAreEqual(List<Move> list1, List<Move> list2)
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
    public bool checkCardsEqualUpToPermutation(List<UniqueCard> list1, List<UniqueCard> list2)
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
    
    // Override Equals method to define equality (this allows search on lists of InfosetNodes)
    //is this still needed?
    public override bool Equals(object obj)
    {
        InfosetNode node = (InfosetNode)obj;
        
        return CheckEquivalentState(node._refState, node._moveHistory);
    }

    //function to check if set of cards are the same up to ordering 
    public static bool AreListsPermutations<T>(List<T> list1, List<T> list2)
    {
        if (list1.Count != list2.Count)
             return false;
        
        //this sorts items into ascending order in terms of hashcode. Note this isnt ideal, ideally
        //we should create a comparison operator on UniqueCards 
        var sortedList1 = list1.OrderBy(x => x.GetHashCode()).ToList();
        var sortedList2 = list2.OrderBy(x => x.GetHashCode()).ToList();
    
        return sortedList1.SequenceEqual(sortedList2);
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
        if ((item1 is Move) && (item2 is Move))
        {
            return EqualsMove(item1 as Move, item2 as Move);
        }
        else if ((item1 is SerializedAgent) && (item2 is SerializedAgent))
        {
            return EqualsAgent(item1 as SerializedAgent, item2 as SerializedAgent);
        }
        else if ((item1 is UniqueEffect) && (item2 is UniqueEffect))
        {
            return EqualsUniqueEffect(item1 as UniqueEffect, item2 as UniqueEffect);
        }
        else if ((item1 is UniqueCard) && (item2 is UniqueCard))
        {
            return EqualsUniqueCard(item1 as UniqueCard, item2 as UniqueCard);
        }
        else
        {
            throw new Exception("Invalid use  of EqualsOverride");
        }
    }
    
    //we need to check equality on moves as part of open loop MCTS algorithm
    private bool EqualsMove(Move move1, Move move2)
    {
        if (!MoveComparer.AreIsomorphic(move1, move2))
            return false;

        return true;
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
    
    //check only card type when seeing if they are equal
    private bool EqualsUniqueCard(UniqueCard card1, UniqueCard card2)
    {
        //if (card1.GetType() != card2.GetType())
        //    return false;
        
        if (card1.CommonId != card2.CommonId)
            return false;

        return true;
    }
    
    // //check for same move
    // private bool EqualsMove(Move move1, Move move2)
    // {
    //     if (move1.Command != move2.Command)
    //         return false;
    //
    //     return true;
    // }

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

// public bool CheckEquivalentState(SeededGameState state)
//     {
//         //to check whether two seeded states are in the same equivalence set we need to check that all the 
//         //visible information for the current player is the same for the SeededGameStates refState and state 
//         
//         //TODO::do we need to check player draw pile and hand are the same up to a permutation?
//         
//         //TODO::this should depend on who the observing player is!
//         
//         //check current player is the same
//         PlayerEnum statePlayerID = state.CurrentPlayer.PlayerID;
//         PlayerEnum refStatePlayerID = _refState.CurrentPlayer.PlayerID;
//         if (statePlayerID != refStatePlayerID)
//             return false;
//         
//         //check board state is the same
//         if (!state.BoardState.Equals(_refState.BoardState))
//             return false;
//         
//         //check coins are the same in both states for both players
//         if (state.CurrentPlayer.Coins != _refState.CurrentPlayer.Coins |
//             state.EnemyPlayer.Coins != _refState.EnemyPlayer.Coins)
//             return false;
//         
//         //check prestige is the same in both states for both players
//         if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
//             state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
//             return false;
//         
//         //check power is the same in both states for both players
//         if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power |
//             state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
//             return false;
//         
//         //check identical patron status. So for the list of patron for this games we check that the favour status for
//         //each patron is the same between our states
//         foreach(PatronId id in state.Patrons)
//         {
//             if (!(state.PatronStates.All[id].Equals(_refState.PatronStates.All[id])))
//                 return false;
//         }
//     
//         //check tavern cards on board are the same up to a permutation
//         if (!AreListsPermutations<UniqueCard>(state.TavernAvailableCards, _refState.TavernAvailableCards))
//             return false;
//         
//         //check current player's hand is the same up to a permutation
//         if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.Hand, _refState.CurrentPlayer.Hand))
//             return false;
//         
//         //check played cards are the same (I think order matters here for played cards - TODO: check this)
//         if (!(state.CurrentPlayer.Played.SequenceEqual(_refState.CurrentPlayer.Played) 
//               | state.EnemyPlayer.Played.SequenceEqual(_refState.EnemyPlayer.Played)))
//             return false;
//         
//         //check that known upcoming draws for the current player are the same (some cards have effects that let a player
//         //know what is about to be drawn)
//         if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.KnownUpcomingDraws, _refState.CurrentPlayer.KnownUpcomingDraws))
//             return false;
//         
//         //check agents on the board are the same for both players up to a permutation. Unfortunately the agent objects have
//         //not had their Equals operation over-ridden and we cant use the default operator as it just check memory address
//         //and we will want to say two instances of the same agent are the same (if they belong to the same player)
//         //so we use a specific function for this check
//         if (!(BespokePermutationsCheck<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
//               && BespokePermutationsCheck<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
//             return false;
//         
//         //The current player will know what is in the cooldown pile both for himself and the enemy as he will have observed
//         //the card being played and then moving to the cooldown pile
//         if (!(AreListsPermutations<UniqueCard>(state.CurrentPlayer.Played, _refState.CurrentPlayer.Played) 
//               && AreListsPermutations<UniqueCard>(state.EnemyPlayer.Played, _refState.EnemyPlayer.Played)))
//             return false;
//
//         //other final items to check, that are based on decisions the current game state is pending
//         if(!(AreListsPermutations<UniqueBaseEffect>(state.UpcomingEffects, _refState.UpcomingEffects)))
//             return false;
//         
//         if(!(AreListsPermutations<UniqueBaseEffect>(state.StartOfNextTurnEffects, _refState.StartOfNextTurnEffects)))
//             return false;
//
//         //check pending choices (not sure we need this if all played cards are the same)
//         if (state.PendingChoice != null && _refState.PendingChoice != null)
//         {
//             if (state.PendingChoice.Type != _refState.PendingChoice.Type)
//                 return false;
//
//             if (state.PendingChoice.Type == Choice.DataType.EFFECT)
//             {
//                 if (!BespokePermutationsCheck<UniqueEffect>(state.PendingChoice.PossibleEffects,
//                         _refState.PendingChoice.PossibleEffects))
//                     return false;
//             }
//         }
//         else
//         {
//             if (state.PendingChoice == null && _refState.PendingChoice != null)
//             {
//                 return false;
//             }
//             else if (state.PendingChoice != null && _refState.PendingChoice == null)
//             {
//                 return false;
//             }
//         }
//         
//         //check end game state. 
//         if (!EqualsGameEndState(state.GameEndState, _refState.GameEndState))
//             return false;
//         
//         //TODO: what is a patron call?
//         if (state.CurrentPlayer.PatronCalls != _refState.CurrentPlayer.PatronCalls)
//             return false;
//         
//         //items we dont check include CompletedActions, as this holds the complete history of the game, and is not needed.
//         //we also dont check combostates, as presumably that is covered by played cards?
//         
//         //if we survive all our tests return true
//         return true;
//     }

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

// public bool sameHiddenInfo(SeededGameState state)
//     {
//         //to check the information hidden to the observing player is equivalent between two game states, we need to check the following
//         //are the same up to a permutation
//         //1. The combined non-observing player's hand and draw pile
//         //2. The observing player's draw pile 
//
//         SerializedPlayer observingPlayerInRefState;
//         SerializedPlayer nonObservingPlayerInRefState;
//         if (ObservingPlayer == _refState.CurrentPlayer.PlayerID)
//         {
//             observingPlayerInRefState = _refState.CurrentPlayer;
//             nonObservingPlayerInRefState = _refState.EnemyPlayer;
//         }
//         else
//         {
//             observingPlayerInRefState = _refState.EnemyPlayer;
//             nonObservingPlayerInRefState = _refState.CurrentPlayer;
//         }
//         
//         SerializedPlayer observingPlayerInState;
//         SerializedPlayer nonObservingPlayerInState;
//         if (ObservingPlayer == state.CurrentPlayer.PlayerID)
//         {
//             observingPlayerInState = state.CurrentPlayer;
//             nonObservingPlayerInState = _refState.EnemyPlayer;
//         }
//         else
//         {
//             observingPlayerInState = state.EnemyPlayer;
//             nonObservingPlayerInState = _refState.CurrentPlayer;
//         }
//         
//         //check that the non-observing player's combined hand and draw pile is the same up to a permutation
//         List<UniqueCard> nonObsHandPlusDrawRefState =
//             nonObservingPlayerInRefState.Hand.Concat(nonObservingPlayerInRefState.DrawPile).ToList();
//         List<UniqueCard> nonObsHandPlusDrawState =
//             nonObservingPlayerInState.Hand.Concat(nonObservingPlayerInState.DrawPile).ToList();
//         if (!(AreListsPermutations<UniqueCard>(nonObsHandPlusDrawState, nonObsHandPlusDrawRefState)))
//             return false;
//             
//         //check that observing player's draw pile is the same in both states up to a permutation
//         if(!(AreListsPermutations<UniqueCard>(observingPlayerInState.DrawPile, observingPlayerInRefState.DrawPile))) 
//             return false;
//         
//         return true;
//     }

 // //Heuristic 'borrowed' from MCTSBot.cs
 //    public double Heuristic(SeededGameState gameState)
 //    {
 //        int finalValue = 0;
 //        int enemyPatronFavour = 0;
 //        foreach (KeyValuePair<PatronId, PlayerEnum> entry in gameState.PatronStates.All)
 //        {
 //            if (entry.Key == PatronId.TREASURY)
 //            {
 //                continue;
 //            }
 //            if (entry.Value == gameState.CurrentPlayer.PlayerID)
 //            {
 //                finalValue += _patronFavour;
 //            }
 //            else if (entry.Value == PlayerEnum.NO_PLAYER_SELECTED)
 //            {
 //                finalValue += _patronNeutral;
 //            }
 //            else
 //            {
 //                finalValue += _patronUnfavour;
 //                enemyPatronFavour += 1;
 //            }
 //        }
 //        if (enemyPatronFavour >= 2)
 //        {
 //            finalValue -= 100;
 //        }
 //
 //        finalValue += gameState.CurrentPlayer.Power * _powerValue;
 //        finalValue += gameState.CurrentPlayer.Prestige * _prestigeValue;
 //        //finalValue += gameState.CurrentPlayer.Coins * _coinsValue;
 //
 //        if (gameState.CurrentPlayer.Prestige < 30)
 //        {
 //            TierEnum tier = TierEnum.UNKNOWN;
 //
 //            foreach (SerializedAgent agent in gameState.CurrentPlayer.Agents)
 //            {
 //                tier = CardTierList.GetCardTier(agent.RepresentingCard.Name);
 //                finalValue += _agentOnBoardValue * (int)tier + agent.CurrentHp * _hpValue;
 //            }
 //
 //            foreach (SerializedAgent agent in gameState.EnemyPlayer.Agents)
 //            {
 //                tier = CardTierList.GetCardTier(agent.RepresentingCard.Name);
 //                finalValue -= _agentOnBoardValue * (int)tier + agent.CurrentHp * _hpValue + _opponentAgentsPenaltyValue;
 //            }
 //
 //            List<UniqueCard> allCards = gameState.CurrentPlayer.Hand.Concat(gameState.CurrentPlayer.Played.Concat(gameState.CurrentPlayer.CooldownPile.Concat(gameState.CurrentPlayer.DrawPile))).ToList();
 //            Dictionary<PatronId, int> potentialComboNumber = new Dictionary<PatronId, int>();
 //            List<UniqueCard> allCardsEnemy = gameState.EnemyPlayer.Hand.Concat(gameState.EnemyPlayer.DrawPile).Concat(gameState.EnemyPlayer.Played.Concat(gameState.EnemyPlayer.CooldownPile)).ToList();
 //            Dictionary<PatronId, int> potentialComboNumberEnemy = new Dictionary<PatronId, int>();
 //
 //            foreach (UniqueCard card in allCards)
 //            {
 //                tier = CardTierList.GetCardTier(card.Name);
 //                finalValue += (int)tier * _cardValue;
 //                if (card.Deck != PatronId.TREASURY)
 //                {
 //                    if (potentialComboNumber.ContainsKey(card.Deck))
 //                    {
 //                        potentialComboNumber[card.Deck] += 1;
 //                    }
 //                    else
 //                    {
 //                        potentialComboNumber[card.Deck] = 1;
 //                    }
 //                }
 //            }
 //
 //            foreach (UniqueCard card in allCardsEnemy)
 //            {
 //                if (card.Deck != PatronId.TREASURY)
 //                {
 //                    if (potentialComboNumberEnemy.ContainsKey(card.Deck))
 //                    {
 //                        potentialComboNumberEnemy[card.Deck] += 1;
 //                    }
 //                    else
 //                    {
 //                        potentialComboNumberEnemy[card.Deck] = 1;
 //                    }
 //                }
 //            }
 //
 //            foreach (KeyValuePair<PatronId, int> entry in potentialComboNumber)
 //            {
 //                finalValue += (int)Math.Pow(entry.Value, _potentialComboValue);
 //            }
 //
 //            foreach (Card card in gameState.TavernAvailableCards)
 //            {
 //                tier = CardTierList.GetCardTier(card.Name);
 //                finalValue -= _penaltyForHighTierInTavern * (int)tier;
 //                /*
 //                if (potentialComboNumberEnemy.ContainsKey(card.Deck) && (potentialComboNumberEnemy[card.Deck]>4) && (tier > TierEnum.B)){
 //                    finalValue -= enemyPotentialComboPenalty*(int)tier;
 //                }
 //                */
 //            }
 //
 //        }
 //
 //        //int finalValue = gameState.CurrentPlayer.Power + gameState.CurrentPlayer.Prestige;
 //        double normalizedValue = NormalizeHeuristic(finalValue);
 //
 //        return normalizedValue;
 //    }
 //
 //    private double NormalizeHeuristic(int value)
 //    {
 //        double normalizedValue = ((double)value - (double)_heuristicMin) / ((double)_heuristicMax - (double)_heuristicMin);
 //
 //        if (normalizedValue < 0)
 //        {
 //            return 0.0;
 //        }
 //
 //        return normalizedValue;
 //    }

 // private double Simulate(InfosetNode startNode, int depth)
    // {
    //     int Count = 0;
    //     Determinisation d = startNode.GetDeterminisation();
    //     double bestPlayoutScore = 0;
    //     int depthCount = 0;
    //     double eps = 0.0001; //used to check for equality on playout scores betwwen nodes
    //     while (d.GetState().GameEndState == null && depthCount <= depth) //GameEndState is only not null for terminal nodes
    //     {
    //         PlayerEnum currentPlayer = d.GetState().CurrentPlayer.PlayerID;
    //             
    //         //create all legal next states 
    //         List<Determinisation> possibleDeterminisations = new List<Determinisation>();
    //         foreach (Move move in d.GetMoves())
    //         {
    //             var (newSeededGameState, newPossibleMoves) = d.GetState().ApplyMove(move);
    //             possibleDeterminisations.Add(new Determinisation(newSeededGameState, newPossibleMoves));
    //         }
    //
    //         //take first move from the list, use asa  reference to capture the case where all payouts are equal for
    //         //the next nodes
    //         Determinisation bestd = possibleDeterminisations[0];
    //         //double playoutScore = playoutHeuristic(bestd.GetState());
    //         //use MCTSBOt heuristic
    //         double playoutScore = _strategy.Heuristic(bestd.GetState());
    //         double firstPlayoutScore = playoutScore;
    //         bestPlayoutScore = firstPlayoutScore;
    //
    //         bool allequal = true;
    //         for (var i = 1; i < possibleDeterminisations.Count; i++) //ignore the first move in this loop 
    //         {
    //             playoutScore = _strategy.Heuristic(possibleDeterminisations[i].GetState());
    //             if (!(playoutScore < (firstPlayoutScore + eps) && playoutScore > (firstPlayoutScore - eps)))
    //             {
    //                 allequal = false;
    //                 if (playoutScore > bestPlayoutScore)
    //                 {
    //                     bestPlayoutScore = playoutScore;
    //                     bestd = possibleDeterminisations[i];
    //                 }
    //             }
    //         }
    //
    //         //if all nodes have the same playout score chose one at random
    //         if (allequal)
    //         {
    //             bestd = possibleDeterminisations.PickRandom(rng);
    //         }
    //
    //         d = bestd;
    //         if (d.GetState().CurrentPlayer.PlayerID != currentPlayer)
    //         {
    //             //player change has occured so increase depthcount
    //             depthCount += 1;
    //         }
    //     }
    //     return bestPlayoutScore;
    // }

 // //functions to simulate moves out to end of player's turn - borrowed from MCTSBot
    // private List<Move> NotEndTurnPossibleMoves(List<Move> possibleMoves)
    // {
    //     return possibleMoves.Where(m => m.Command != CommandEnum.END_TURN).ToList();
    // }
    //
    // //functions to simulate moves out to end of player's turn - borrowed from MCTSBot
    // private Move DrawNextMove(List<Move> possibleMoves, SeededGameState gameState, SeededRandom rng)
    // {
    //     Move nextMove;
    //     List<Move> notEndTurnPossibleMoves = NotEndTurnPossibleMoves(possibleMoves);
    //     if (notEndTurnPossibleMoves.Count > 0)
    //     {
    //         if ((gameState.BoardState == BoardState.NORMAL) && (Extensions.RandomK(0, 10000, rng) == 0))
    //         {
    //             nextMove = Move.EndTurn();
    //         }
    //         else
    //         {
    //             nextMove = notEndTurnPossibleMoves.PickRandom(rng);
    //         }
    //     }
    //     else
    //     {
    //         nextMove = Move.EndTurn();
    //     }
    //     return nextMove;
    // }
    //
    // //functions to simulate moves out to end of player's turn - borrowed from MCTSBot
    // public double SimulateToEndOfPlayersTurn(InfosetNode startNode)
    // {
    //     Determinisation startd = startNode.GetDeterminisation();
    //     if (startNode._currentMoveFromParent.Command == CommandEnum.END_TURN)
    //     {
    //         return _strategy.Heuristic(startd.GetState());
    //     }
    //
    //     List<Move> notEndTurnPossibleMoves = NotEndTurnPossibleMoves(startd.GetMoves());
    //     Move nextMove;
    //     if (notEndTurnPossibleMoves.Count > 0)
    //     {
    //         if ((startd.GetState().BoardState == BoardState.NORMAL) && (Extensions.RandomK(0, 100000, rng) == 0))
    //         {
    //             nextMove = Move.EndTurn();
    //         }
    //         else
    //         {
    //             nextMove = notEndTurnPossibleMoves.PickRandom(rng);
    //         }
    //     }
    //     else
    //     {
    //         nextMove = Move.EndTurn();
    //     }
    //
    //     var gameState = startd.GetState();
    //     var (seedGameState, newMoves) = gameState.ApplyMove(nextMove);
    //     nextMove = DrawNextMove(newMoves, seedGameState, rng);
    //
    //     while (nextMove.Command != CommandEnum.END_TURN)
    //     {
    //
    //         var (newSeedGameState, newPossibleMoves) = seedGameState.ApplyMove(nextMove);
    //         nextMove = DrawNextMove(newPossibleMoves, newSeedGameState, rng);
    //         seedGameState = newSeedGameState;
    //     }
    //
    //     //MCTSBot used Heuristic(gameState) instead of Heuristic(seedGameState), looks like a bug?
    //     return _strategy.Heuristic(seedGameState);
    // }