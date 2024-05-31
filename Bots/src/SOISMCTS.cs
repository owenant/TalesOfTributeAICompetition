using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using ScriptsOfTribute.Board.Cards;

namespace Bots;

//class to implement single observer information set monte carlo tree search
public class SOISMCTS : AI
{
    //note the seed should be removed so that it is based off the system clock, once the code is working
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private TimeSpan _TurnTimeout = TimeSpan.FromSeconds(10.0);
    SeededRandom rng = new(123); //TODO: initialise using clock when code complete

    private void PrepareForGame()
    { 
        //if any agent set-up needed it can be done here
        
        //TODO: can we initialise and store a static tree object so that we can re-use the tree from 
        //move to move?
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
        //we assume that the observing player is fixed for our game tree, and is the player controlled by our bot
        PlayerEnum observingPlayer = gameState.CurrentPlayer.PlayerID;
        
        //Initialise a single node information set tree using the GameState object and list of possible moves. These can
        //then be used to generate a random determinisation (concrete state and legal moves pair) of the root information
        //set node on each iteration of the monte carlo loop. Note we include the possible moves list in our tree constructor
        //as we will use pass around state plus move pairs in the form of a determinisation struct object.
        //Seems tidier than keeping track of them separately and risking inconsistencies.
        ISTree tree = new ISTree(gameState,  possibleMoves, observingPlayer);
        
        //in ISMCTS each iteration of the loop starts with a new determinisation we use to explore the SAME tree
        //updating the stats for each tree node (which are information sets as seen by a chosen observer, in our
        //case our bot)
        Stopwatch s = new Stopwatch();
        s.Start();
        while (s.Elapsed < _timeForMoveComputation)
        {
            //create a random determinisation from the tree root
            Determinisation d0 = tree.GetRootDeterminisation();
            ISNode v0 = tree._root;
            
            //enter selection routine - return a node along with a determinisation (compatible state and move set
            //ultimately derived from d0)
            Tuple<ISNode, Determinisation> nodeAndDeterminisation = select(tree, v0, d0);
            ISNode v = nodeAndDeterminisation.Item1;
            Determinisation d = nodeAndDeterminisation.Item2;
        }
        
        return possibleMoves.PickRandom(rng);
    }
    
    public Tuple<ISNode, Determinisation> select(ISTree tree, ISNode v, Determinisation d)
    { 
        //descend our IS tree (restricted to nodes/actions compatible with d)using our chosen tree policy
        //until a node is reached such that some action from that node leads to
        //an observing player information set that is not currently in the tree or the node v is terminal
        
        double val = 0;
        double bestVal = 0;
        ISNode bestNode = v;
        List<ISAction> uvd = tree.GetActionsWithNoTreeNodes(v, d);
        while (d.state.GameEndState != null && uvd.Count == 0)
        {
            List<ISNode> cvd = tree.GetCompatibleChildrenInTree(v, d);
            foreach(ISNode node in cvd)
            {
                val = TreePolicy(node);
                if (val > bestVal)
                {
                    bestVal = val;
                    bestNode = node;
                }
            }
            v = bestNode;
            //TODO: update determinisation
            ISAction incomingAction = tree.GetIncomingActionForNodeInTree(v);
            //TODO: shift from our Action to List<Moves> used by engine
            //var (newSeedGameState, newPossibleMoves) = seedGameState.ApplyMove(nextMove);
            
            d = d;
            uvd = tree.GetActionsWithNoTreeNodes(v, d);
        }
        Tuple<ISNode, Determinisation> selectedNodeAndDeterminisation = new Tuple<ISNode, Determinisation>(v, d);
        return selectedNodeAndDeterminisation;
    }

    private double TreePolicy(ISNode node)
    {
        return 0;
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
    }
}

//Encapsulates directed graph, where nodes are information sets with an equivalence relation based on the 
//provided observing player
public class ISTree
{ 
    //our information set tree is a connected graph, with each node consisting of states of the game that are
    //information set equivalent for a given observing player and edges corresponding to actions from a player
    //whose action it is in the current state that move our game from one state to the next
    public List<ISNode> _treeNodes;
    public List<ISAction> _treeActions;
    public GameState _rootGameState;
    public List<Move> _rootMovesList;
    public PlayerEnum _observingPlayer;
    public ISNode _root;
    private readonly SeededRandom _rng = new(123); //TODO: initialise using clock when code complete
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public ISTree(GameState refGameState, List<Move> possibleMoves, PlayerEnum observingPlayer)
    {
        _treeNodes = new List<ISNode>();
        _treeActions = new List<ISAction>();
        _rootGameState = refGameState;
        _rootMovesList = possibleMoves;
        _observingPlayer = observingPlayer;
        
        //need to generate a reference state to set-up the root information set node (not this does not need to be
        //the same as the random determinisation from the start of each monet carlo iteration, but by definition should
        //be an equivalent state
        Determinisation d = GetRootDeterminisation();
        
        _root = new ISNode(d.state, observingPlayer); 
        _treeNodes.Add(_root);
    }

    public Determinisation GetRootDeterminisation()
    {
        SeededGameState s = _rootGameState.ToSeededGameState((ulong) _rng.Next());
        
        //note that the root moves list contains the legal moves for the current player in our seeded game state
        //(the randomisation only affects hidden information and the current player can see his cards, and the list of possible moves
        //is based of the current players hand)
        Determinisation d = new Determinisation(s, _rootMovesList);

        return d;
    }
    
    //get children of a node (which are info sets) compatible with a determinatisation d, which are contained in 
    //our tree (equivalent of c(v,d) in paper pseudo-code)
    public HashSet<ISNode> GetCompatibleChildrenInTree(ISNode v, Determinisation d)
    {
        //first we get compatible actions
        HashSet<ISAction> compatibleActions = v.GetCompatibleActions(d);
        
        //then for each action we check if the end node is in the tree or not
        HashSet<ISNode> compatibleChildren = new HashSet<ISNode>();
        foreach (ISAction action in compatibleActions)
        {
            if (_treeNodes.Contains(action._endISNode))
            {
                compatibleChildren.Add(action._endISNode);
            }
        }
        return compatibleChildren;
    }
    
    //get actions from a node v and determinisation d, for which v does not have children in the tree
    //equivalent to function u(v,d) in pseudo-code
    public HashSet<ISAction> GetActionsWithNoTreeNodes(ISNode v, Determinisation d)
    {
        //generate all actions compatible with determinisation d
        HashSet<ISAction> compatibleActions = v.GetCompatibleActions(d); //Note this is also run as part of c(v,d) which isnt very efficient
        
        //then for each action that can be generated from d, check to see if that action is in our tree,
        //if not then add it out list of actions with no tree nodes
        HashSet<ISAction> actionsWithNoTreeNode = new HashSet<ISAction>();
        foreach (ISAction action in compatibleActions)
        {
            if (!_treeActions.Contains(action))
            {
                actionsWithNoTreeNode.Add(action);
            }
        }
        return actionsWithNoTreeNode;
    }

    public ISAction GetIncomingActionForNodeInTree(ISNode v)
    {
        //check node is in tree
        foreach (ISAction action in _treeActions)
        {
             if (action._endISNode.Equals(v))
             {
                 return action;
             }
        } 
        throw new Exception("GetIncomingActionForNodeInTree: Node not found in ISTree");
    }
}

//Node for an ISTree, each node corresponds to an information set for the observing player for a tree
public class ISNode
{
    //reference state, i.e. one instance of the set of equivalent states, however this will be in a 'canonical form'
    //to support hash based searches 
    private SeededGameState _refState; //(do we need to change this to a determinisation and if so that isn't the equivalence class)
    private PlayerEnum _observingPlayer;
    
    //create a node using a reference state for the information set it encapsulates
    public ISNode(SeededGameState refState, PlayerEnum playerObserving)
    {
        //in order to support hashing we convert the refState into a 'canonical state'. Hashing then speeds up
        //searching for nodes
        this._refState = ConvertToCanonical(refState);
        this._observingPlayer = playerObserving;
    }

    public SeededGameState ConvertToCanonical(SeededGameState state)
    {
        //we need to make sure that all elements of a game state have a well defined order (both for hidden and non-hidden information)
        
        //what happens if hidden information is different? ISNode should not care but GetHashCode will care....
        //this is oK, just means we still need to define a function which correctly implements the equivalence relation
        
        
        //we define a canonical state as one where all the elements of the seededGameSate are in a particualr order
        //this needs to be true for both hidden and non-hidden information.
        //note this does not change the legal moves for the state
        //note this needs to be only done once when a tree node is constructed and this will then support all future search operations
        //converting Contains() from O(n) to O(1) (via GetHashCode)
        
        //first we deal with information visible to the current player
        
        
        //then for information that is hideen to the current player
        
        
        return state;
    }

    //check if a state is part of the equivalence class for this node
    public bool CheckEquivalentState(SeededGameState state)
    {
        //TODO: Does the equals function work ok for seeded game states?
        return _refState.Equals(ConvertToCanonical(state));
    }

    //get actions available from info set, compatible with determinisation d. This is A(d) 
    //in pseudo-code. 
    public HashSet<ISAction> GetCompatibleActions(Determinisation d)
    {
        //first check that the game state in d is in our information set
        HashSet<ISAction> compatibleActions = new HashSet<ISAction>();
        if (!this.CheckEquivalentState(d.state))
        {
            //in this case return an empty set
            return compatibleActions;
        }
        
        //generate all possible states from d with the list of possible moves
        List<SeededGameState> possibleStates = new List<SeededGameState>();
        foreach (Move move in d.moves)
        {
            var (newSeedGameState, newPossibleMoves) = d.state.ApplyMove(move);
            possibleStates.Add(newSeedGameState);
        }
        
        //next for each possible new state compatible with our determinisation d we generate an ISNode and a
        //corresponding action being careful not to duplicate.
        foreach (SeededGameState state in possibleStates)
        {
            ISNode newNode = new ISNode(state, _observingPlayer);
            ISAction newAction = new ISAction(this, newNode);

            if (!compatibleActions.Contains(newAction))
            {
                compatibleActions.Add(newAction);
            }
        }

        return compatibleActions;
    }
    
    // Override Equals method to define equality
    public override bool Equals(object obj)
    {
        ISNode node = (ISNode)obj;

        //return (this.CheckEquivalentState(node._refState) && this._observingPlayer == node._observingPlayer);
        //note the following is reliant on our refernce states being in a canonical form and therefore can be directly
        //compared ot one another.
        //TODO::Is the equals function OK to use on the SeededGameState object?
        return (this._refState.Equals(node._refState) && this._observingPlayer == node._observingPlayer);
    }

    // Override GetHashCode method to ensure consistency with Equals
    public override int GetHashCode()
    {
        //note this is reliant on our reference states for our nodes being converted into a canonical form
        //so that _refState is the same for any two nodes with the same state equivalence classes
        return (this._refState, this._observingPlayer).GetHashCode();
    }
}

//class to encapsulate links between ISNodes, these correspond to an action that can can be taken for the
//player to move from a starting information set to an end info set. Each action is an equivalence class of edges
//so that many moves between states can correspond to a single action 
public class ISAction
{
    public ISNode _startISNode;
    public ISNode _endISNode;
    
    public ISAction(ISNode startNode, ISNode endNode)
    {
        this._startISNode = startNode;
        this._endISNode = endNode;
    }
    
    // Override Equals method to define equality
    public override bool Equals(object obj)
    {
        ISAction action = (ISAction)obj;
        return (_startISNode.Equals(action._startISNode) && _endISNode.Equals(action._endISNode));
    }

    // Override GetHashCode method to ensure consistency with Equals
    public override int GetHashCode()
    {
        return (_startISNode, _endISNode).GetHashCode();
    }
}

//struct to encapsulate a specific determinisation, which includes a concrete game state and compatible moves
public struct Determinisation
{
    public SeededGameState state;
    public List<Move> moves;

    public Determinisation(SeededGameState gamestate, List<Move> compatibleMoves)
    {
        state = gamestate;
        moves = compatibleMoves;
    }
}

  /*//check whether or not a given state is in the equivalence set of states defined by this ISNode
    //this is redudant due to moving to using a  canconical reference state....
    public bool CheckEquivalentState(SeededGameState state)
    {
        //to check whether two seeded states are in the same equivalence set we need to check that all the 
        //visible information for the current player is the same for the SeededGameStates refState and state 
        //TODO: check entries in state to see if anything missing from list below
        //TODO: Should we check the cool down pile as well? Is this known for both players?
        
        //check current player is the same
        if (state.CurrentPlayer.PlayerID != _refState.CurrentPlayer.PlayerID)
        
        //check board state is the same
        if (state.BoardState != _refState.BoardState)
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
        if (state.CurrentPlayer.Power != _refState.CurrentPlayer.Power|
            state.EnemyPlayer.Power != _refState.EnemyPlayer.Power)
            return false;
        
        //TODO: check these permutations functions work as expected.
        //check tavern cards on board are the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.TavernAvailableCards, _refState.TavernAvailableCards))
            return false;
        
        //check current player's hand is the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.Hand, _refState.CurrentPlayer.Hand))
            return false;
        
        //check played cards are the same up to a permutation (TODO:: or should they just be the same? does order matter for played cards?)
        if (!(AreListsPermutations<UniqueCard>(state.CurrentPlayer.Played, _refState.CurrentPlayer.Played) 
              && AreListsPermutations<UniqueCard>(state.EnemyPlayer.Played, _refState.EnemyPlayer.Played)))
            return false;
        
        //check that known upcoming draws for the current player are the same (what are these? presumably current player
        //does not know upcoming draws for enemy player?)
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.KnownUpcomingDraws, _refState.CurrentPlayer.KnownUpcomingDraws))
            return false;
        
        //check agents on the board are the same for both players up to a permutation
        if (!(AreListsPermutations<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
              && AreListsPermutations<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
            return false;
        
        //check identical patron status. So for the list of patron for this games we check that the favour status for
        //each patron is the same between our states
        foreach(PatronId id in state.Patrons)
        {
            if (state.PatronStates.All[id] != _refState.PatronStates.All[id])
                return false;
        }

        //other items to check?
        //state.CompletedActions;
        //state.UpcomingEffects;
        //state.StartOfNextTurnEffects;
        //state.PendingChoice;
        //state.GameEndState;
        //state.CurrentPlayer.CooldownPile;
        //state.CurrentPlayer.PatronCalls;
        //state.ComboStates
        
        //if we survive all our tests return true
        return true;
    }*/
    
    // //function to check if set of cards are the same up to ordering
    // public static bool AreListsPermutations<T>(List<T> list1, List<T> list2)
    // {
    //     if (list1.Count != list2.Count)
    //         return false;
    //
    //     var sortedList1 = list1.OrderBy(x => x).ToList();
    //     var sortedList2 = list2.OrderBy(x => x).ToList();
    //
    //     return sortedList1.SequenceEqual(sortedList2);
    // }