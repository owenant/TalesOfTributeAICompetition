using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;

namespace Bots;

//class to implement single observer information set monte carlo tree search
public class SOISMCTS : AI
{
    private readonly SeededRandom rng = new(123);
    public override PatronId SelectPatron(List<PatronId> availablePatrons, int round)
    {
        return availablePatrons.PickRandom(rng);
    }

    public override Move Play(GameState gameState, List<Move> possibleMoves, TimeSpan remainingTime)
    {
        //Initialise an information set based tree using current game state and the assumed observing player
        FairSerializedPlayer observingPlayer = gameState.CurrentPlayer;
        ISTree tree = new ISTree(gameState, observingPlayer); 
        
        return possibleMoves.PickRandom(rng);
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
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public ISTree(GameState state, FairSerializedPlayer observingPlayer)
    {
        this._nodes = new List<ISNode>();
        ISNode rootNode = new ISNode(state, observingPlayer);
        _nodes.Add(rootNode);
    }
    
}

//Node for an ISTree, each node corresponds to an information set for the observing player for a tree
public class ISNode
{
    private ISEquivalentStatesSet _infoSet;
    
    //create a node using a reference state for the information set it encapsulates
    public ISNode(GameState refState, FairSerializedPlayer playerObserving)
    {
        this._infoSet = new ISEquivalentStatesSet(refState, playerObserving);
    }
    
    //return a random state from our node
    public GameState Determinisation()
    {
        return _infoSet.Determinsation();
    }
}

//class to encapsulate links between ISNodes, these correspond to possible moves for the player about to play taking
//the game state from a start node to an end node using a connectingMove
public class ISLink
{
    public ISNode _startNode;
    public ISNode _endNode;
    public Move _connectingMove;

    public ISLink(ISNode sourceNode, ISNode destinationNode, Move connectMove)
    {
        this._startNode = sourceNode;
        this._endNode = destinationNode;
        this._connectingMove = connectMove;
    }
}

//this class can be constructed for a single reference state and then provide states that are information set
//equivalent for the observing player 
public class ISEquivalentStatesSet
{
    //reference state, i.e. one instance of the set of equivalent states
    private GameState _refState;
    private FairSerializedPlayer _observingPlayer;

    public ISEquivalentStatesSet(GameState state, FairSerializedPlayer playerObserving)
    {
        this._refState = state;
        this._observingPlayer = playerObserving;
    }

    //returns a random state for our set of equivalent states
    public GameState Determinsation()
    {
        //TODO: How do we do this?
        return this._refState;
    }
}