using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using Newtonsoft.Json.Serialization;

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
        
        Stopwatch s = new Stopwatch();
        s.Start();
        while (s.Elapsed < _timeForMoveComputation)
        {
            //select a random game state from the information set root node of the tree with a uniform distribution
            //we will use only nodes and actions compatible with this state
            ISNode root = tree.GetRootNode();
            SeededGameState d0 = root.GetDeterminisation((ulong) rng.Next());
            
            //enter selection routine - descend our IS tree (restricted to nodes/actions compatible with d0)
            //using our chosen tree policy until a node is reached such that some action from that node leads to
            //an observing player information set that is not currently in the tree or the node v is terminal
            Tuple<ISNode, SeededGameState> nodeGameState = select(tree, d0);
            ISNode v = nodeGameState.Item1;
            SeededGameState d = nodeGameState.Item2;
        }
        
        return possibleMoves.PickRandom(rng);
    }
    
    
    public Tuple<ISNode, SeededGameState> select(ISTree v0, SeededGameState d0)
    {
        
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
    
    //get all links from an IS node
    
    
    //get all links from a node compatible with a determinisation d
    
    
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
    
    //get children of a node (which are info sets) compatible with a determinatisation d
    //equivalent of set c(v,d) in paper pseudo-code
    public getCompatibleChildren(SeededGameState d)
    {
        
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
//4/ All edges in an action have the same player about to start (corresponding to player for start ISNode)
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
