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
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private readonly TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private readonly TimeSpan _TurnTimeout = TimeSpan.FromSeconds(30.0);
    private readonly SeededRandom rng = new(123); //TODO: initialise using clock when code complete

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
        InfosetTree tree = new InfosetTree(gameState,  possibleMoves, observingPlayer);
        
        //in InfosetMCTS each iteration of the loop starts with a new determinisation we use to explore the SAME tree
        //updating the stats for each tree node (which are information sets as seen by a chosen observer, in our
        //case our bot)
        Stopwatch s = new Stopwatch();
        s.Start();
        while (s.Elapsed < _timeForMoveComputation)
        {
            //create a random determinisation from the tree root and store alongside root
            //information set node
            InfosetNodeState nodeState = tree.GetRootDeterminisation();
            
            //enter selection routine - return a node along with a determinisation 
            InfosetNodeState selectedNode = select(tree, nodeState);
        }
        
        return possibleMoves.PickRandom(rng);
    }
    
    public InfosetNodeState select(InfosetTree tree, InfosetNodeState nodeState)
    { 
        //descend our infoset tree (restricted to nodes/actions compatible with d) using our chosen tree policy
        //until a node is reached such that some action from that node leads to
        //an observing player information set that is not currently in the tree or the node v is terminal
        InfosetNodeState bestNodeState = nodeState;
        List<InfosetActionState> uvd = tree.GetActionsWithNoTreeNodes(bestNodeState);
        
        while (bestNodeState.d.state.GameEndState != null && uvd.Count == 0)
        {
            List<InfosetNodeState> cvd = tree.GetCompatibleChildrenInTree(bestNodeState);
            double bestVal = 0;
            foreach(InfosetNodeState node in cvd)
            {
                double val = TreePolicy(node.v);
                if (val > bestVal)
                {
                    bestVal = val;
                    bestNodeState = node;
                }
            }
            uvd = tree.GetActionsWithNoTreeNodes(bestNodeState);
        }
        return bestNodeState;
    }

    private double TreePolicy(InfosetNode node)
    {
        
        return 0;
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
    }
}

//Encapsulates directed graph, where nodes are information sets with an equivalence relation based on the 
//provided observing player
public class InfosetTree
{ 
    //our information set tree is a connected graph, with each node consisting of states of the game that are
    //information set equivalent for a given observing player and edges corresponding to actions from a player
    //whose action it is in the current state that move our game from one state to the next
    public List<InfosetNode> TreeNodes;
    public List<InfosetAction> TreeActions;
    public GameState RootGameState;
    public List<Move> RootMovesList;
    public PlayerEnum ObservingPlayer;
    public InfosetNode RootNode;
    private readonly SeededRandom _rng = new(123); //TODO: initialise using clock when code complete
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public InfosetTree(GameState refGameState, List<Move> possibleMoves, PlayerEnum observingPlayer)
    {
        TreeNodes = new List<InfosetNode>();
        TreeActions = new List<InfosetAction>();
        RootGameState = refGameState;
        RootMovesList = possibleMoves;
        ObservingPlayer = observingPlayer;
        
        //need to generate a reference state to set-up the root information set node (not this does not need to be
        //the same as the random determinisation from the start of each monet carlo iteration, but by definition should
        //be an equivalent state
        InfosetNodeState nodeState = GetRootDeterminisation();
        RootNode = nodeState.v;
        TreeNodes.Add(RootNode);
    }

    public InfosetNodeState GetRootDeterminisation()
    {
        SeededGameState s = RootGameState.ToSeededGameState((ulong) _rng.Next());
        
        //note that the root moves list contains the legal moves for the current player in our seeded game state
        //(the randomisation only affects hidden information and the current player can see his cards, and the list of possible moves
        //is based off the current players hand)
        Determinisation d = new Determinisation(s, RootMovesList);
        
        //note this next step isnt strictly necessary, but might be abit less confusing that the
       // reference state for the root equivalence class is set to be equal to the determinisation
       //used during the monte carlo loop
       RootNode = new InfosetNode(d.state, ObservingPlayer);
       
       return new InfosetNodeState(RootNode, d);
    }
    
    //get children of a node (which are info sets) compatible with a given determinisation, which are contained in 
    //our tree (equivalent of c(v,d) in paper pseudo-code)
    public List<InfosetNodeState> GetCompatibleChildrenInTree(InfosetNodeState nodeState)
    {
        //first we get compatible actions
        List<InfosetActionState> compatibleActions = nodeState.v.GetCompatibleActions(nodeState.d);
        
        //then for each action we check if the end node is in the tree or not
        List<InfosetNodeState> compatibleChildren = new List<InfosetNodeState>();
        foreach (InfosetActionState actionState in compatibleActions)
        {
            if (TreeNodes.Contains(actionState.action._endInfosetNode))
            {
                compatibleChildren.Add(new InfosetNodeState(actionState.action._endInfosetNode, actionState.d));
            }
        }
        return compatibleChildren;
    }
    
    //get actions from a node v and determinisation d, for which v does not have children in the tree
    //equivalent to function u(v,d) in pseudo-code
    public List<InfosetActionState> GetActionsWithNoTreeNodes(InfosetNodeState nodeState)
    {
        //first we get compatible actions
        List<InfosetActionState> compatibleActions = nodeState.v.GetCompatibleActions(nodeState.d);

        //then for each action that can be generated from d, check to see if that action is in our tree,
        //if not then add it out list of actions with no tree nodes
        List<InfosetActionState> actionsWithNoTreeNode = new List<InfosetActionState>();
        foreach (InfosetActionState actionState in compatibleActions)
        {
            if (!TreeActions.Contains(actionState.action))
            {
                actionsWithNoTreeNode.Add(new InfosetActionState(actionState.action,actionState.d));
            }
        }
        return actionsWithNoTreeNode;
    }
}

//Node for an InfosetTree, each node corresponds to an information set for the observing player for a tree
//Note we do not implement a GetHashCode method for this object and hence it can not be stored as part of a HashSet.
//The reason for this is that we would need to define a 'canonical reference state' for our equivalence class that could 
//be hashed and lead to the same hash number across InfosetNodes instances which contain the same equivalence class. 
//This would mean agreeing rules for ordering of all data items both hidden and non-hidden information. This is possible,
//however InfosetNodes are only used in the selection step of the MCTS algorithm, so the extent of the speed up coming from
//the trade off of being able to do O(1) search on HashSets (as opposed ot O(N) on Lists) versus creating canonical
//reference states is not clear. This maybe something to explore to optimise the execution time of this bot.
public class InfosetNode
{
    //reference state, i.e. one instance of the set of equivalent states
    private SeededGameState _refState; //(do we need to change this to a determinisation and if so that isn't the equivalence class)
    private PlayerEnum _observingPlayer;
    
    //create a node using a reference state for the information set it encapsulates
    public InfosetNode(SeededGameState refState, PlayerEnum playerObserving)
    {
        this._refState = refState;
        this._observingPlayer = playerObserving;
    }
    
    //check if a state is part of the equivalence class for this node
    public bool CheckEquivalentState(SeededGameState state)
    {
        //to check whether two seeded states are in the same equivalence set we need to check that all the 
        //visible information for the current player is the same for the SeededGameStates refState and state 
        
        //check current player is the same
        if (state.CurrentPlayer.PlayerID != _refState.CurrentPlayer.PlayerID)
            return false;
        
        //check board state is the same
        if (state.BoardState.Equals(_refState.BoardState))
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
            if (state.PatronStates.All[id] != _refState.PatronStates.All[id])
                return false;
        }
    
        //check tavern cards on board are the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.TavernAvailableCards, _refState.TavernAvailableCards))
            return false;
        
        //check current player's hand is the same up to a permutation
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.Hand, _refState.CurrentPlayer.Hand))
            return false;
        
        //check played cards are the same (I think order matters here for played cards - TODO: check this)
        if (state.CurrentPlayer.Played.SequenceEqual(_refState.CurrentPlayer.Played) 
              | state.EnemyPlayer.Played.SequenceEqual(_refState.EnemyPlayer.Played))
            return false;
        
        //check that known upcoming draws for the current player are the same (some cards have effects that let a player
        //know what is about to be drawn)
        if (!AreListsPermutations<UniqueCard>(state.CurrentPlayer.KnownUpcomingDraws, _refState.CurrentPlayer.KnownUpcomingDraws))
            return false;
        
        //check agents on the board are the same for both players up to a permutation. 
        if (!(AreListsPermutations<SerializedAgent>(state.CurrentPlayer.Agents, _refState.CurrentPlayer.Agents) 
              && AreListsPermutations<SerializedAgent>(state.EnemyPlayer.Agents, _refState.EnemyPlayer.Agents)))
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
        
        if(!(state.PendingChoice.Equals(_refState.PendingChoice)))
            return false;
        
        //check end game state. 
        if (!(state.GameEndState.Equals(_refState.GameEndState)))
            return false;
        
        //TODO: what is a patron call?
        if (state.CurrentPlayer.PatronCalls != _refState.CurrentPlayer.PatronCalls)
            return false;
        
        //items we dont check include CompletedActions, as this holds the complete history of the game, and is not needed.
        //we also dont check combostates, as presumably that is covered by played cards?
        
        //if we survive all our tests return true
        return true;
    }
    
    //get actions available from info set, compatible with a determinisation d.This is A(d) in pseudo-code. 
    public List<InfosetActionState> GetCompatibleActions(Determinisation d)
    {
        //generate all possible determinisations from d with the list of possible moves
        List<Determinisation> possibleDeterminisations = new List<Determinisation>();
        foreach (Move move in d.moves)
        {
            var (newSeedGameState, newPossibleMoves) = d.state.ApplyMove(move);
            Determinisation dNew = new Determinisation(newSeedGameState, newPossibleMoves);
            
            possibleDeterminisations.Add(dNew);
        }
        
        //next for each possible new state compatible with our determinisation d we generate an InfosetNode and a
        //corresponding action being careful not to duplicate.
        List<InfosetAction> compatibleActions = new List<InfosetAction>();
        List<InfosetActionState> compatibleActionStates = new List<InfosetActionState>();
        foreach (Determinisation dNew  in possibleDeterminisations)
        {
            InfosetNode newNode = new InfosetNode(dNew.state, _observingPlayer);
            InfosetAction newAction = new InfosetAction(this, newNode);
            if (!compatibleActions.Contains(newAction))
            {
                InfosetActionState newActionState = new InfosetActionState(newAction, dNew);
                compatibleActions.Add(newAction);
                compatibleActionStates.Add(newActionState);
            }
        }

        return compatibleActionStates;
    }
    
    // Override Equals method to define equality (this allows search on lists of InfosetNodes)
    public override bool Equals(object obj)
    {
        InfosetNode node = (InfosetNode)obj;
        
        return (this.CheckEquivalentState(node._refState) && this._observingPlayer == node._observingPlayer);
    }

    //function to check if set of cards are the same up to ordering 
    public static bool AreListsPermutations<T>(List<T> list1, List<T> list2)
    {
        if (list1.Count != list2.Count)
             return false;
        
        //this sorts items into ascending order in terms of hashcode. Note this isnt ideal, ideally
        //we should create a comparison operator on UniqueCards and Serialised Agents (which are the main two objects 
        //that substitute for T in the above)
        var sortedList1 = list1.OrderBy(x => x.GetHashCode()).ToList();
        var sortedList2 = list2.OrderBy(x => x.GetHashCode()).ToList();
    
        return sortedList1.SequenceEqual(sortedList2);
    }
}

//class to encapsulate links between InfosetNodes, these correspond to an action that can can be taken for the
//player to move from a starting information set to an end info set. Each action is an equivalence class of edges
//so that many moves between states can correspond to a single action 
public class InfosetAction
{
    public InfosetNode _startInfosetNode;
    public InfosetNode _endInfosetNode;
    
    public InfosetAction(InfosetNode startNode, InfosetNode endNode)
    {
        this._startInfosetNode = startNode;
        this._endInfosetNode = endNode;
    }
    
    // Override Equals method to define equality (note we dont over-ride GetHashcode as we have not done this for 
    //the InfosetNode object)
    public override bool Equals(object obj)
    {
        InfosetAction action = (InfosetAction)obj;
        return (_startInfosetNode.Equals(action._startInfosetNode) && _endInfosetNode.Equals(action._endInfosetNode));
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

//we also use a struct to manage a pair consisting of an infosetnode and a determinisation
//this is used as we trace down the tree using a particular root determinisation (and it's
//descendent)
public struct InfosetNodeState
{
    public InfosetNode v;
    public Determinisation d;

    public InfosetNodeState(InfosetNode node, Determinisation deter)
    {
        v = node;
        d = deter;
    }
}

//similarly we define an action determinisation pair, where the determinisation corresponds to the
//determinisation of the final node of the action
public struct InfosetActionState
{
    public InfosetAction action;
    public Determinisation d;

    public InfosetActionState(InfosetAction infosetaction, Determinisation deter)
    {
        action = infosetaction;
        d = deter;
    }
}
