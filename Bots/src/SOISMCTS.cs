using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using System.Collections.Generic;

namespace Bots;

//class to implement single observer information set monte carlo tree search
public class SOISMCTS : AI
{
    //note the seed should be removed so that it is based off the system clock, once the code is working
    private readonly SeededRandom rng = new(123);
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private TimeSpan _TurnTimeout = TimeSpan.FromSeconds(10.0);
    private PlayerEnum _myID;
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
        _myID = gameState.CurrentPlayer.PlayerID;
        //Initialise a single node information set  tree using current game state as the root node
        //and the assumed observing player
        ISTree tree = new ISTree(gameState, _myID); 
        
        //select a random game state from the information set root node of the tree with a uniform distribution
        //we will use only nodes and actions compatible with this state
        ISNode v = tree.GetRootNode();
        SeededGameState state = v.GetRandomEquivalentState((ulong) rng.Next());
        //TODO:: get moves compatible with this determinisation (use IsLegalMove function?)
        List<Move> compatibleMoves = possibleMoves;
        Determinisation d = new Determinisation(state, compatibleMoves);
        
        Stopwatch s = new Stopwatch();
        s.Start();
        while (s.Elapsed < _timeForMoveComputation)
        {
            //enter selection routine - return a node along with determinisation and compatible move set
            Tuple<ISNode, Determinisation> nodeAndDeterminisation = select(tree, v, d);
            v = nodeAndDeterminisation.Item1;
            d = nodeAndDeterminisation.Item2;
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
        HashSet<Action> uvd = tree.GetActionsWithNoTreeNodes(v, d);
        while (d.state.GameEndState != null && uvd.Count == 0)
        {
            HashSet<ISNode> cvd = tree.GetCompatibleChildrenInTree(v, d);
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
    public List<ISNode> _nodes;
    public List<ISAction> _actions;
    private ISNode _root;
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public ISTree(GameState state, PlayerEnum observingPlayer)
    {
        this._nodes = new List<ISNode>();
        ISNode rootNode = new ISNode(state, observingPlayer);
        _root = rootNode;
        _nodes.Add(rootNode);
    }

    public ISNode GetRootNode()
    {
        return _root;
    }
    
    //get children of a node (which are info sets) compatible with a determinatisation d
    //(equivalent of c(v,d) in paper pseudo-code)
    public HashSet<ISNode> GetCompatibleChildrenInTree(ISNode v, Determinisation d)
    {
        HashSet<ISNode> compatibleChildren = new HashSet<ISNode>();
        //var (newSeedGameState, newPossibleMoves) = seedGameState.ApplyMove(nextMove);
        foreach (ISAction action in _actions)
        {
            if (action._startISNode.Equals(v))
            {
                //check linking action is compatible with determinisation 
                if (action.CheckCompatibleAction(d))
                {
                    compatibleChildren.Add(action._endISNode);
                }
            }
        }
        return compatibleChildren;
    }
    
    //get actions from a node v and determinisation d, for which v does not have children in the tree
    //equivalent to function u(v,d) in pseudo-code
    public HashSet<ISAction> GetActionsWithNoTreeNodes(ISNode v, Determinisation d)
    {
        HashSet<ISAction> compatibleActions = v.GetCompatibleActions(d);
        HashSet<ISAction> actionsWithNoTreeNode = new HashSet<Action>();

        foreach (ISAction action in compatibleActions)
        {
            if (!_nodes.Contains(action._endISNode))
            {
                actionsWithNoTreeNode.Add(action);
            }
        }
        return actionsWithNoTreeNode;
    }

    public ISAction GetIncomingActionForNodeInTree(ISNode v)
    {
        //check node is in tree
        foreach (ISAction action in _actions)
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
    //reference state, i.e. one instance of the set of equivalent states
    private GameState _refState;
    private PlayerEnum _observingPlayer;
    
    //create a node using a reference state for the information set it encapsulates
    public ISNode(GameState refState, PlayerEnum playerObserving)
    {
        this._refState = refState;
        this._observingPlayer = playerObserving;
    }
    
    //returns a random state from our set of equivalent states
    public SeededGameState GetRandomEquivalentState(ulong seed)
    {
        //need to return a uniformly random pick from states in our equivalence set
        
        //TODO: does this actually return a random state from a uniform distribution across
        //our information set?
        return _refState.ToSeededGameState(seed);
    }

    //get actions available from info set, compatible with determinisation d. This is A(d) 
    //in pseudo-code. Strictly speaking the state d does not need to be a determinisation of
    //an info set, hence A is only a function of d. But coding wise it seems appropriate to place
    //the method here
    public HashSet<Action> GetCompatibleActions(Determinisation d)
    {
        compatibleActions
        //first check that the game state in d is in our information set
        if (!this.CheckEquivalentState(d.state))
        {
            
        }
        HashSet<Action> compatibleActions = new HashSet<Action>();
        return compatibleActions;
    }

    // Override Equals method to define equality
    public override bool Equals(object obj)
    {
        ISNode node = (ISNode)obj;
        //check if reference states are equivalent
        if (this._refState.Equals(node._refState) && node._observingPlayer == this._observingPlayer)
        {
            return true;
        }
        else
        {
            return false;
        }
    }

    //check whether or not a given state is in the equivalence set of states defined by this ISNode
    public bool CheckEquivalentState(SeededGameState state)
    {
        //TODO: How do we do this? need to check visible information is equivalent
        //check current player is the same etc
        return false;
    }

    // Override GetHashCode method to ensure consistency with Equals
    public override int GetHashCode()
    {
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
    
    //check this action is compatible with a particular determinisation
    public bool CheckCompatibleAction(Determinisation d)
    {
        //check state for determinisation d is contained within the starting ISNode
        bool checkState = _startISNode.CheckEquivalentState(d.state);
        
        //check that at least one of the compatible moves for our determinisation corresponds
        //to an edge that is in the equivalence class defined by this action
        bool checkMoves = false;
        foreach (Move move in d.moves)
        {
            var (newSeedGameState, newPossibleMoves) = d.state.ApplyMove(move);
            Edge edge = new Edge(d.state, newSeedGameState);
            if (this.CheckEquivalentEdge(edge))
            {
                checkMoves = true;
                break;
            }
        }

        if (checkState && checkMoves)
        {
            return true;
        }
        else
        {
            return false;
        }
    }
    
    //function to check if a given edge belong to the equivalence class defined by this action.
    //Each edge is a link between two specific game states . Two edges are equivalent if:
    //1. They do not belong to the same starting state (TODO: Why?, Also our ISNode has no specific starting state)
    //2. Both edges have the same player about to start 
    //3. The start info set is the same for both edges
    //4. The end info set is the same for both edges
    public bool CheckEquivalentEdge(Edge edge)
    {
        //check that the start info set is the same for both edges. To do this we can check that the
        //starting game state in the edge belongs to the state equivalence class defined in the starting ISNode
        //This also includes the check that both have the same player about to move
        if (!_startISNode.CheckEquivalentState(edge._startState))
        {
            return false;
        }
        //check that the end info set is the same for both edges.
        if (!_endISNode.CheckEquivalentState(edge._endState))
        {
            return false;
        }
        return true;
    }
}

//this defines a specific move between two concrete game states 
public class Edge
{
    public SeededGameState _startState;
    public SeededGameState _endState;
    public Edge(SeededGameState startState, SeededGameState endState)
    {
        this._startState = startState;
        this._endState = endState;
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
