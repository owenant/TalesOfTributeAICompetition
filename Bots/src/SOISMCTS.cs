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
        
        //create a determinisation of the current game state
        SeededGameState rootRefState = gameState.ToSeededGameState((ulong) rng.Next());
        
        //Initialise a single node information set tree using the seeded game state as a reference state
        //for the set of equivalent states that define the root node
        ISTree tree = new ISTree(rootRefState, observingPlayer);
        ISNode v = tree._root;
        
        //TODO:: get moves compatible with this determinisation (use IsLegalMove function?)
        List<Move> compatibleMoves = possibleMoves;
        Determinisation d = new Determinisation(rootRefState, compatibleMoves);
        
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
        HashSet<ISAction> uvd = tree.GetActionsWithNoTreeNodes(v, d);
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
    public List<ISNode> _treeNodes;
    public List<ISAction> _treeActions;
    public ISNode _root;
    public PlayerEnum _observingPlayer;
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public ISTree(SeededGameState refState, PlayerEnum observingPlayer)
    {
        _treeNodes = new List<ISNode>();
        _root = new ISNode(refState, observingPlayer);
        _treeNodes.Add(_root);
        _observingPlayer = observingPlayer;
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
    //reference state, i.e. one instance of the set of equivalent states
    private SeededGameState _refState;
    private PlayerEnum _observingPlayer;
    
    //create a node using a reference state for the information set it encapsulates
    public ISNode(SeededGameState refState, PlayerEnum playerObserving)
    {
        this._refState = refState;
        this._observingPlayer = playerObserving;
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

    //check whether or not a given state is in the equivalence set of states defined by this ISNode
    public bool CheckEquivalentState(SeededGameState state)
    {
        //TODO: How do we do this? need to check visible information is equivalent
        //check current player is the same etc
        return false;
    }
    
    // Override Equals method to define equality
    public override bool Equals(object obj)
    {
        ISNode node = (ISNode)obj;

        return (this.CheckEquivalentState(node._refState) && this._observingPlayer == node._observingPlayer);
    }

    // Override GetHashCode method to ensure consistency with Equals
    public override int GetHashCode()
    {
        //TODO: how do we do this when the reference state might be changed to any other equivalent state to give us the 
        //same node?
        //return (this._refState, this._observingPlayer).GetHashCode();
        return 0;
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
   // public bool CheckCompatibleAction(Determinisation d)
   //{
   //     //check state for determinisation d is contained within the starting ISNode
   //     bool checkState = _startISNode.CheckEquivalentState(d.state);
//
//        //check that at least one of the compatible moves for our determinisation corresponds
//        //to an edge that is in the equivalence class defined by this action
//        bool checkMoves = false;
//        foreach (Move move in d.moves)
//        {
//            var (newSeedGameState, newPossibleMoves) = d.state.ApplyMove(move);
//            Edge edge = new Edge(d.state, newSeedGameState);
//            if (this.CheckEquivalentEdge(edge))
//            {
//                checkMoves = true;/               break;
// /          }
//        }
//
//        if (checkState && checkMoves)
//        {
//            return true;
//        }
//        else
//        {
//            return false;
//        }
//    }
    
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
