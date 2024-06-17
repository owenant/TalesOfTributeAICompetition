using System.Data.SqlTypes;
using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using System.Dynamic;
using System.Runtime;
using ScriptsOfTribute.Board.Cards;
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
    
    //counter for number of times play method is called
    private int playMethodCallCount = 0;
    
    //parameters for MCTS
    private readonly double K = 1.0; //explore vs exploit parameter for tree policy
    private readonly int maxSimulationDepth = 2; //only explore current player and next player response

    public static bool errorcheck = false;
    
    private void PrepareForGame()
    { 
        //if any agent set-up needed it can be done here
        
        //seed random number generator
        long seed = DateTime.Now.Ticks;
        //rng = new(123);  //TODO: initialise using clock when code complete
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
        
        //increment game counter
        _gameCounter += 1;

        //TODO: can we initialise and store a static tree object so that we can re-use the tree from 
        //move to move
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
        }

        //Initialise a root node
        SeededGameState s = gameState.ToSeededGameState((ulong) rng.Next());
        Determinisation d = new Determinisation(s, possibleMoves); //not possible moves are compatible with all seeds at the root
        InfosetNode root = new InfosetNode(null, null, d);
        
        //in InfosetMCTS each iteration of the loop starts with a new determinisation we use to explore the SAME tree
        //updating the stats for each tree node (which are information sets as seen by a chosen observer, in our
        //case our bot)
        Move chosenMove = null;
        if (possibleMoves.Count == 1 && possibleMoves[0].Command == CommandEnum.END_TURN)
        {
            _startOfTurn = true;
            _usedTimeInTurn = TimeSpan.FromSeconds(0);
            _turnCounter += 1;
            _moveCounter += 1;
            return possibleMoves[0];
        }

        if (_usedTimeInTurn + _timeForMoveComputation >= _turnTimeout)
        {
            chosenMove = possibleMoves.PickRandom(rng);
        }
        else
        {
            int actionCounter = 0;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            while (timer.Elapsed < _timeForMoveComputation)
            {
                //create a random determinisation 
                s = gameState.ToSeededGameState((ulong) rng.Next());
                d = new Determinisation(s, possibleMoves); //not possible moves are compatible with all seeds at the root
                //and set as determinisation to use for this iteration
                root.SetDeterminisationAndParentMove(d, null);

                //enter selection routine - return an array of nodes with index zero corresponding ot root and final
                //entry corresponding to the node for expansion
                List<InfosetNode> pathThroughTree = select(root);

                //if selected node has children not in the tree then expand the tree
                InfosetNode selectedNode = pathThroughTree[pathThroughTree.Count -1];
                var (cvd, uvd) = selectedNode.GetChildrenInTreeAndMovesNotInTree();
                InfosetNode expandedNode = selectedNode;
                if (uvd.Count != 0)
                {
                    expandedNode = Expand(selectedNode);
                    pathThroughTree.Add(expandedNode);
                }

                //next we simulate our playouts from our expanded node 
                double payoutFromExpandedNode = Simulate((Determinisation) expandedNode.GetDeterminisation(), maxSimulationDepth);

                //next we complete the backpropagation step
                BackPropagation(payoutFromExpandedNode, pathThroughTree);

                _simsCounter += 1;
                //noIterations += 1;
            }
            _usedTimeInTurn += _timeForMoveComputation;

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
    
    //returns the sleected path through the tree
    public List<InfosetNode> select(InfosetNode startNode)
    { 
        //descend our infoset tree (restricted to nodes/actions compatible with d associated with our start node)
        //using our chosen tree policy until a node is reached such that some action from that node leads to
        //information set that is not currently in the tree or the node v is terminal
        InfosetNode bestNode = startNode;
        var (cvd, uvd) = startNode.GetChildrenInTreeAndMovesNotInTree();
        //this contains each node passed through in this iteration 
        List<InfosetNode> pathThroughTree = new List<InfosetNode>();
        pathThroughTree.Add(startNode);
        while (bestNode.GetDeterminisation().GetState().GameEndState == null && uvd.Count == 0)
        {
            double bestVal = 0;
            foreach(InfosetNode node in cvd)
            {
                double val = TreePolicy(node);
                if (val > bestVal)
                {
                    bestVal = val;
                    bestNode = node;
                }
            }
            (cvd, uvd) = bestNode.GetChildrenInTreeAndMovesNotInTree();
            pathThroughTree.Add(bestNode);
        }
        return pathThroughTree;
    }

    //uvd is the set of actions from 
    private InfosetNode Expand(InfosetNode node)
    {
        //choose a move at random from our and add child node to tree
        var (cvd, uvd) = node.GetChildrenInTreeAndMovesNotInTree();
        Move move = uvd.PickRandom(rng);
        var (newSeededGameState, newMoves) = node.GetDeterminisation().GetState().ApplyMove(move);
        Determinisation newd = new Determinisation(newSeededGameState, newMoves);
        InfosetNode newNode = node.CreateChild(move, newd);
        
        //return new infosetNode object, corresponding to new child containing the new determinisation
        return newNode;
    }

    //simulate our game from a given determinisation (ignoring information sets)
    private double Simulate(Determinisation d0, int depth)
    {
        int Count = 0;
        Determinisation d = d0;
        double bestPlayoutScore = 0;
        int depthCount = 0;
        double eps = 0.0001; //used to check for equality on playout scores betwwen nodes
        while (d.GetState().GameEndState == null && depthCount <= depth) //GameEndState is only not null for terminal nodes
        {
            PlayerEnum currentPlayer = d.GetState().CurrentPlayer.PlayerID;
                
            //create all legal next states 
            List<Determinisation> possibleDeterminisations = new List<Determinisation>();
            foreach (Move move in d.GetMoves())
            {
                var (newSeededGameState, newPossibleMoves) = d.GetState().ApplyMove(move);
                possibleDeterminisations.Add(new Determinisation(newSeededGameState, newPossibleMoves));
            }

            //take first move from the list, use asa  reference to capture the case where all payouts are equal for
            //the next nodes
            Determinisation bestd = possibleDeterminisations[0];
            double playoutScore = playoutHeuristic(bestd.GetState());
            double firstPlayoutScore = playoutScore;
            bestPlayoutScore = firstPlayoutScore;

            bool allequal = true;
            for (var i = 1; i < possibleDeterminisations.Count; i++) //ignore the first move in this loop 
            {
                playoutScore = playoutHeuristic(possibleDeterminisations[i].GetState());
                if (!(playoutScore < (firstPlayoutScore + eps) && playoutScore > (firstPlayoutScore - eps)))
                {
                    allequal = false;
                    if (playoutScore > bestPlayoutScore)
                    {
                        bestPlayoutScore = playoutScore;
                        bestd = possibleDeterminisations[i];
                    }
                }
            }

            //if all nodes have teh same playout score chose one at random
            if (allequal)
            {
                bestd = possibleDeterminisations.PickRandom(rng);
            }

            d = bestd;
            Count += 1;
            if (d.GetState().CurrentPlayer.PlayerID != currentPlayer)
            {
                //player change has occured so increase depthcount
                depthCount += 1;
            }
        }
        return bestPlayoutScore;
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
            node.AvailabilityCount += 1;
            var (cvd, uvd)  = node.GetChildrenInTreeAndMovesNotInTree();
            foreach (InfosetNode child in cvd)
            {
                child.AvailabilityCount += 1;
            }
        }
    }
    
    private double TreePolicy(InfosetNode node)
    {
        double nodeUCB = node.UCB(K);
        return nodeUCB;
    }

    //chooses child from root node with highest visitation number
    public Move chooseBestMove(InfosetNode rootNode)
    {
        //note that for the root node, all possible moves are compatible with any determinisation
        //as it is the observing player's turn to go. Also the move that was used to last go from the root
        //to the best node would be the same as any other move to go between these nodes 
        int bestVisitCount = 0;
        Move bestMove = null;
        foreach (InfosetNode node in rootNode.Children)
        {
            if (node.VisitCount > bestVisitCount)
            {
                bestVisitCount = node.VisitCount;
                bestMove = node._currentMoveFromParent;
            }   
        }
        return bestMove;
    }

    //heuristic used to choose each move in simulate function. Currently we choose moves that maximise prestige
    private double playoutHeuristic(SeededGameState state)
    {
        double prestige = state.CurrentPlayer.Prestige;
        if (state.GameEndState != null)
        {
            if (state.GameEndState.Winner == state.CurrentPlayer.PlayerID)
            {
                return prestige * 1.5;
            }
            else
            {
                return prestige * 0.5;
            }
        }
        else
        {
            return prestige;
        }
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
        double avgMovesPerTurn = _moveCounter/ (1.0 * _turnCounter);
        double avgSimsPerMove = _simsCounter / (1.0 * _moveCounter);
        
        string message = "Game count: " + _gameCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Turn Counter: " + _turnCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of moves per turn: " + avgMovesPerTurn.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of simulations per move: " + avgSimsPerMove;
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
    private List<Move>? _currentMovesWithNoChildren; //stores moves form current determinsation that have no children
    private List<InfosetNode>? _compatibleChildrenInTree; //list of children compatible with current determinisation that are in the list of children
    public double TotalReward;
    public int VisitCount;
    public int AvailabilityCount;
    
    //create a node using a reference determinisation for information set it encapsulates
    public InfosetNode(InfosetNode? parent, Move? moveFromParent, Determinisation d)
    {
        //when a node is first instatntiated a reference state is stored to define in the information set equivalence class
        _refState = d.GetState();
        
        //initialise values for UCB calc
        TotalReward = 0.0001;
        VisitCount = 1;
        AvailabilityCount = 1;

        Parent = parent;
        Children = new List<InfosetNode>();
        
        this.SetDeterminisationAndParentMove(d, moveFromParent);
    }

    //this method updates the current determinisation and parent move for a node, and then 
    //for each of the nodes furtehr down the tree sets to null the current determinisation
    //and parent move, so that they get recalculated when calling GetChildrenInTreeAndMovesNotInTree
    public void SetDeterminisationAndParentMove(Determinisation? d, Move? fromParent)
    {
        _currentDeterminisation = d;
        _currentMoveFromParent = fromParent;
        _compatibleChildrenInTree = null;
        _currentMovesWithNoChildren = null;
        
        //also need to set to null anything to do with the determinisation for all nodes further down the tree form this one.
        foreach (InfosetNode node in Children)
        {
            node.SetDeterminisationAndParentMove(null, null);
        }
    }

    public Determinisation? GetDeterminisation()
    {
        return _currentDeterminisation;
    }

    //calculate upper confidence bound for trees, bandit algorithm for MCTS tree policy
    public double UCB(double K)
    {
        double normalisedReward = TotalReward / 80.0; //80 is the upper limit for a tie break
        double ucbVal = normalisedReward / (VisitCount * 1.0) + K * Math.Sqrt(Math.Log((AvailabilityCount * 1.0) / (VisitCount * 1.0) ));
        return ucbVal;
    }
    
    //returns lists of moves in current determinisation for which there are no children in the tree
    //and list of moves for which there are no children
    public Tuple<List<InfosetNode>, List<Move>> GetChildrenInTreeAndMovesNotInTree()
    {
        if (_compatibleChildrenInTree != null && _currentMovesWithNoChildren != null)
            return new Tuple<List<InfosetNode>, List<Move>>(_compatibleChildrenInTree, _currentMovesWithNoChildren);
        
        _compatibleChildrenInTree = new List<InfosetNode>();
        _currentMovesWithNoChildren = new List<Move>();
        foreach (Move move in _currentDeterminisation.GetMoves())
        {
            var (newState, newMoves) = _currentDeterminisation.GetState().ApplyMove(move);
            
            //find if new state is in tree or not
            bool found = false;
            foreach (InfosetNode child in Children)
            {
                if (child.CheckEquivalentState(newState))
                {
                    //found child node that is an equivalent state
                    found = true;
                    child.SetDeterminisationAndParentMove(new Determinisation(newState, newMoves), move); 
                    _compatibleChildrenInTree.Add(child);
                }
            }

            if (!found)
            {
                _currentMovesWithNoChildren.Add(move);
            }
        }

        return new Tuple<List<InfosetNode>, List<Move>>(_compatibleChildrenInTree, _currentMovesWithNoChildren);
    }
    
    public InfosetNode CreateChild(Move parentMove, Determinisation newd)
    {
        InfosetNode childNode = new InfosetNode(this, parentMove, newd);
        Children.Add(childNode);

        return childNode;
    }
    
    //check if a state is part of the equivalence class for this node
    public bool CheckEquivalentState(SeededGameState state)
    {
        //to check whether two seeded states are in the same equivalence set we need to check that all the 
        //visible information for the current player is the same for the SeededGameStates refState and state 
        
        //check current player is the same
        PlayerEnum statePlayerID = state.CurrentPlayer.PlayerID;
        PlayerEnum refStatePlayerID = _refState.CurrentPlayer.PlayerID;
        if (statePlayerID != refStatePlayerID)
            return false;
        
        //check board state is the same
        if (!state.BoardState.Equals(_refState.BoardState))
            return false;
        
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
    
        //check tavern cards on board are the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.TavernAvailableCards, _refState.TavernAvailableCards))
            return false;
        
        //check current player's hand is the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.Hand, _refState.CurrentPlayer.Hand))
            return false;
        
        //check played cards are the same (I think order matters here for played cards - TODO: check this)
        if (!(state.CurrentPlayer.Played.SequenceEqual(_refState.CurrentPlayer.Played) 
              | state.EnemyPlayer.Played.SequenceEqual(_refState.EnemyPlayer.Played)))
            return false;
        
        //check that known upcoming draws for the current player are the same (some cards have effects that let a player
        //know what is about to be drawn)
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.KnownUpcomingDraws, _refState.CurrentPlayer.KnownUpcomingDraws))
            return false;
        
        //check agents on the board are the same for both players up to a permutation. Unfortunately the agent objects have
        //not had their Equals operation over-ridden and we cant use the default operator as it just check memory address
        //and we will want to say two instances of the same agent are the same (if they belong to the same player)
        //so we use a specific function for this check
        if (!(BespokePermutationsCheck<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
              && BespokePermutationsCheck<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
            return false;
        
        //The current player will know what is in the cooldown pile both for himself and the enemy as he will have observed
        //the card being played and then moving to the cooldown pile
        if (!(AreListsPermutations<UniqueCard>(state.CurrentPlayer.Played, _refState.CurrentPlayer.Played) 
              && AreListsPermutations<UniqueCard>(state.EnemyPlayer.Played, _refState.EnemyPlayer.Played)))
            return false;

        //other final items to check, that are based on decisions the current game state is pending
        if(!(AreListsPermutations<UniqueBaseEffect>(state.UpcomingEffects, _refState.UpcomingEffects)))
            return false;
        
        if(!(AreListsPermutations<UniqueBaseEffect>(state.StartOfNextTurnEffects, _refState.StartOfNextTurnEffects)))
            return false;

        //check pending choices (not sure we need this if all played cards are the same)
        if (state.PendingChoice != null && _refState.PendingChoice != null)
        {
            if (state.PendingChoice.Type != _refState.PendingChoice.Type)
                return false;

            if (state.PendingChoice.Type == Choice.DataType.EFFECT)
            {
                if (!BespokePermutationsCheck<UniqueEffect>(state.PendingChoice.PossibleEffects,
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
    
    // Override Equals method to define equality (this allows search on lists of InfosetNodes)
    public override bool Equals(object obj)
    {
        InfosetNode node = (InfosetNode)obj;
        
        return CheckEquivalentState(node._refState);
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

    public bool EqualsOverride<T>(T item1, T item2)
    {
        if ((item1 is SerializedAgent) && (item2 is SerializedAgent))
        {
            return EqualsAgent(item1 as SerializedAgent, item2 as SerializedAgent);
        }
        else if ((item1 is UniqueEffect) && (item2 is UniqueEffect))
        {
            return EqualsEffect(item1 as UniqueEffect, item2 as UniqueEffect);
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

    private bool EqualsEffect(UniqueEffect effect1, UniqueEffect effect2)
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


