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
        SeededGameState d = v.GetDeterminisation((ulong) rng.Next());
        //TODO:: get moves compatible with this determinisation
        List<Move> compatibleMoves = possibleMoves;
        
        Stopwatch s = new Stopwatch();
        s.Start();
        while (s.Elapsed < _timeForMoveComputation)
        {
            //enter selection routine - return a node along with determinisation and compatible move set
            Tuple<ISNode, SeededGameState, List<Move>> nodeGameStateAndMoves = select(tree, v, d, compatibleMoves);
            v = nodeGameStateAndMoves.Item1;
            d = nodeGameStateAndMoves.Item2;
            compatibleMoves = nodeGameStateAndMoves.Item3;
        }
        
        return possibleMoves.PickRandom(rng);
    }
    
    
    public Tuple<ISNode, SeededGameState> select(ISTree tree, ISNode v, SeededGameState d, List<Move> compatibleMoves)
    { 
        //descend our IS tree (restricted to nodes/actions compatible with d)using our chosen tree policy
        //until a node is reached such that some action from that node leads to
        //an observing player information set that is not currently in the tree or the node v is terminal
        
        double val = 0;
        double bestVal = 0;
        ISNode bestNode = v;
        HashSet<Action> uvd = tree.GetActionsWithNoTreeNodes(v, d);
        while (d.GameEndState != null && uvd.Count == 0)
        {
            HashSet<ISNode> cvd = tree.GetCompatibleChildrenInTree(v, d, compatibleMoves);
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
        Tuple<ISNode, SeededGameState> selectedNodeAndState = new Tuple<ISNode, SeededGameState>(v, d);
        return selectedNodeAndState;
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
    public List<ISLink> _links;
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
    public HashSet<ISNode> GetCompatibleChildrenInTree(ISNode v, SeededGameState d, List<Move> compatibleMoves)
    {
        HashSet<ISNode> compatibleChildren = new HashSet<ISNode>();
        //var (newSeedGameState, newPossibleMoves) = seedGameState.ApplyMove(nextMove);
        foreach (ISLink link in _links)
        {
            if (link._startISNode.Equals(v))
            {
                
                return link._connectingISAction;
            }
        }

        return compatibleChildren;
    }
    
    //get actions from a node v and determinisation d, for which v does not have children in the tree
    //equivalent to function u(v,d) in pseudo-code
    public HashSet<Action> GetActionsWithNoTreeNodes(ISNode v, SeededGameState d)
    {
        HashSet<Action> compatibleActions = v.GetCompatibleActions(d);
        
        //TODO:find actions that have no node in the tree
        HashSet<Action> actionWithNoTreeNode = new HashSet<Action>();
        
        return actionWithNoTreeNode;
    }

    public ISAction GetIncomingActionForNodeInTree(ISNode v)
    {
        //check node is in tree
        foreach (ISLink link in _links)
        {
             if (link._endISNode.Equals(v))
             {
                 return link._connectingISAction;
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
    
    //returns a random state (determinisation) for our set of equivalent states
    public SeededGameState GetDeterminisation(ulong seed)
    {
        //need to return a uniformly random pick from states in our equivalene set
        
        //TODO: does this actually return a random state from a uniform distribution across
        //our information set?
        return _refState.ToSeededGameState(seed);
    }

    //get actions available from info set, compatible with determinisation d. This is A(d) 
    //in pseudo-code. Strictly speaking the state d does not need to be a determinisation of
    //an info set, hence A is only a function of d. But coding wise it seems appropriate to place
    //the method here
    public HashSet<Action> GetCompatibleActions(SeededGameState d)
    {
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

    // Override GetHashCode method to ensure consistency with Equals
    public override int GetHashCode()
    {
        return (this._refState, this._observingPlayer).GetHashCode();
    }
}

//class to encapsulate links between ISNodes, these correspond to an action that can can be taken for the
//player to move from a starting information set to an end info set
public class ISLink
{
    public ISNode _startISNode;
    public ISNode _endISNode;
    public ISAction _connectingISAction;

    public ISLink(ISNode sourceNode, ISNode destinationNode, ISAction connectISAction)
    {
        this._startISNode = sourceNode;
        this._endISNode = destinationNode;
        this._connectingISAction = connectISAction;
    }
}

//an action is an equivalence class of edges. Each edge is a link between two specific game states 
//(determinisations). Two edges are equivalent if 1. They do not belong to the same starting determinisation
//2. The start info set is the same for both edges 3. The end info set is the same for both edges
//4 All edges in an action class have the same player about to start (corresponding to player for start ISNode)
//TODO: I dont understand point 1?
public class ISAction
{
    private ISNode _startISNode;
    private ISNode _endISNode;
    private Edge _refEdge;
    
    public ISAction(ISNode startNode, ISNode endNode, Edge refEdge)
    {
        this._startISNode = startNode;
        this._endISNode = endNode;
        this._refEdge = refEdge;
        
        //TODO: check start and end state of refEdge are part of the startNode and endNode information sets
    }
    
    //check this action is compatible with a particular determinisation of the startNode

}

//this defines a specific move between two concrete game states or determinisations
public class Edge
{
    private SeededGameState _startState;
    private SeededGameState _endState;
    private Move _move;
    public Edge(SeededGameState startState, SeededGameState endState, Move playerMove)
    {
        this._startState = startState;
        this._endState = endState;
        this._move = playerMove;
    }
}
