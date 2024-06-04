using System.Data.SqlTypes;
using ScriptsOfTribute;
using ScriptsOfTribute.AI;
using ScriptsOfTribute.Board;
using ScriptsOfTribute.Serializers;
using System.Diagnostics;
using System.Dynamic;
using System.Runtime;
using ScriptsOfTribute.Board.Cards;

namespace Bots;

//class to implement single observer information set monte carlo tree search
public class SOISMCTS : AI
{
    private TimeSpan _usedTimeInTurn = TimeSpan.FromSeconds(0);
    private readonly TimeSpan _timeForMoveComputation = TimeSpan.FromSeconds(0.3);
    private readonly TimeSpan _TurnTimeout = TimeSpan.FromSeconds(30.0);
    private readonly SeededRandom rng = new(123); //TODO: initialise using clock when code complete
    
    //parameters for MCTS
    private readonly double K = 1.0; //explore vs exploit parameter for tree policy

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
            InfosetNode rootNode = tree.GetRootDeterminisation();
            
            //enter selection routine - return a selected node ready to expand, along with a set of actions
            //that have no nodes in in the tree
            Tuple<InfosetNode, List<InfosetAction>, List<InfosetNode>> selectedNodeActionsNotInTreeAndPath = select(tree, rootNode);
            
            //if selected node has children not in the tree then expand the tree
            InfosetNode selectedNode = selectedNodeActionsNotInTreeAndPath.Item1;
            List<InfosetAction> actionsNotInTree = selectedNodeActionsNotInTreeAndPath.Item2;
            List<InfosetNode> pathThroughTree =  selectedNodeActionsNotInTreeAndPath.Item3;
            InfosetNode expandedNode = selectedNode;
            if (actionsNotInTree.Count != 0)
            {
                expandedNode = Expand(tree, actionsNotInTree);
                pathThroughTree.Add(expandedNode);
            }
            
            //next we simulate our playouts from our expanded node 
            double payoutFromExpandedNode = Simulate(expandedNode.GetDeterminisation());
            
            //finally we complete the backpropagation step
            BackPropagation(tree, payoutFromExpandedNode, pathThroughTree);
        }
        
        return possibleMoves.PickRandom(rng);
    }
    
    //returns the following:
    // 1. selected node to expand
    // 2. Set of actions from selected node that dont have any children in the tree (could be empty set if node is terminal)
    // 3. The path taken down the tree from the root node to the final selected node 
    public Tuple<InfosetNode, List<InfosetAction>, List<InfosetNode>> select(InfosetTree tree, InfosetNode startNode)
    { 
        //descend our infoset tree (restricted to nodes/actions compatible with d associated with our node)
        //using our chosen tree policy until a node is reached such that some action from that node leads to
        //information set that is not currently in the tree or the node v is terminal
        InfosetNode bestNode = startNode;
        List<InfosetAction> uvd = tree.GetActionsWithNoTreeNodes(bestNode);

        Determinisation visitingDeterminisation = bestNode.GetDeterminisation();
        List<InfosetNode> pathThroughTree = new List<InfosetNode>();
        while (visitingDeterminisation.state.GameEndState != null && uvd.Count == 0)
        {
            List<InfosetNode> cvd = tree.GetCompatibleChildrenInTree(bestNode);
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
            uvd = tree.GetActionsWithNoTreeNodes(bestNode);
            pathThroughTree.Add(bestNode);
            visitingDeterminisation = bestNode.GetDeterminisation();
        }
        return new Tuple<InfosetNode, List<InfosetAction>, List<InfosetNode>>(bestNode, uvd, pathThroughTree);
    }

    private double TreePolicy(InfosetNode node)
    {
        return node.UCB(K);
    }

    //uvd is the set of actions from 
    private InfosetNode Expand(InfosetTree tree, List<InfosetAction> actionsNotInTree)
    {
        //choose an acton at random from our actions not in the tree
        var random = new Random();
        var randomIndex = random.Next(0, actionsNotInTree.Count);
        
        //add child node to tree
        InfosetAction action = actionsNotInTree[randomIndex];
        InfosetNode childNode = action._endInfosetNode;
        tree.TreeNodes.Add(childNode);
        tree.TreeActions.Add(action);
        
        //return new infosetNode object, corresponding to new child containing the new determinisation
        return childNode;
    }

    //simulate our game from a given determinisation (ignoring information sets)
    private double Simulate(Determinisation d0)
    {
        Determinisation d = d0;
        double bestPlayoutScore = 0;
        while (d.state.GameEndState != null)
        {
            double playoutScore = 0;
            bestPlayoutScore = 0;
            Determinisation bestd = d;
            foreach(Move move in d.moves)
            {
                var (newSeededGameState, newPossibleMoves) = d.state.ApplyMove(move);
                playoutScore = playoutHeuristic(newSeededGameState);
                if (playoutScore > bestPlayoutScore)
                {
                    bestPlayoutScore = playoutScore;
                    bestd = new Determinisation(newSeededGameState, newPossibleMoves);
                }
            }
            d = bestd;
        }

        return bestPlayoutScore;
    }
    
    //function too backpropagate simulation playout results
    private void BackPropagation(InfosetTree tree, double finalPayout, List<InfosetNode> pathThroughTree)
    {
        //need to traverse tree from the playout start node back up to the root of the tree
        //note that our path through the tree should hold references to tree nodes and hence can be updated directly
        //(program design could be improved here!)
        foreach (InfosetNode node in pathThroughTree)
        {
            node.VisitCount += 1;
            node.TotalReward += finalPayout;
            node.AvailabilityCount += 1;
            List<InfosetNode> children = tree.GetCompatibleChildrenInTree(node);
            foreach (InfosetNode child in children)
            {
                child.AvailabilityCount += 1;
            }
        }
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
    private GameState _rootGameState;
    private List<Move> _rootMovesList;
    private InfosetNode _rootNode;
    private readonly SeededRandom _rng = new(123); //TODO: initialise using clock when code complete
    
    //constructor to initialise a tree with a root node using the current gameState as a reference state for the 
    //corresponding information set
    public InfosetTree(GameState refGameState, List<Move> possibleMoves, PlayerEnum observingPlayer)
    {
        TreeNodes = new List<InfosetNode>();
        TreeActions = new List<InfosetAction>();
        _rootGameState = refGameState;
        _rootMovesList = possibleMoves;
        
        //need to generate a reference state to set-up the root information set node (not this does not need to be
        //the same as the random determinisation from the start of each monet carlo iteration, but by definition should
        //be an equivalent state)
        _rootNode = GetRootDeterminisation();
        TreeNodes.Add(_rootNode);
    }

    public InfosetNode GetRootDeterminisation()
    {
        SeededGameState s = _rootGameState.ToSeededGameState((ulong) _rng.Next());
        
        //note that the root moves list contains the legal moves for the current player in our seeded game state
        //(the randomisation only affects hidden information and the current player can see his cards, and the list of possible moves
        //is based off the current players hand)
        Determinisation d = new Determinisation(s, _rootMovesList);
        
        //need to update the visiting determinisation for the root node
        _rootNode.SetDeterminisation(d);
        
        return _rootNode;
    }
    
    //get children of a node (which are info sets) compatible with the visiting determinisation for the node,
    //which are contained in our tree (equivalent of c(v,d) in paper pseudo-code)
    public List<InfosetNode> GetCompatibleChildrenInTree(InfosetNode node)
    {
        //first we get compatible actions - using the determinisation associated with the node
        List<InfosetAction> compatibleActions = node.GetCompatibleActions();
        
        //then for each action we check if the end node is in the tree or not
        List<InfosetNode> compatibleChildren = new List<InfosetNode>();
        foreach (InfosetAction action in compatibleActions)
        {
            int index = TreeNodes.IndexOf(action._endInfosetNode);
            if (index >= 0)
            {
                compatibleChildren.Add(TreeNodes[index]);
            }
        }
        return compatibleChildren;
    }
    
    //get actions from a node v and determinisation d (associated with the node), for which v does not have children
    //in the tree equivalent to function u(v,d) in pseudo-code
    public List<InfosetAction> GetActionsWithNoTreeNodes(InfosetNode node)
    {
        //first we get compatible actions
        List<InfosetAction> compatibleActions = node.GetCompatibleActions();

        //then for each action that can be generated from d, check to see if that action is in our tree,
        //if not then add it out list of actions with no tree nodes
        List<InfosetAction> actionsWithNoTreeNode = new List<InfosetAction>();
        foreach (InfosetAction action in compatibleActions)
        {
            int index = TreeActions.IndexOf(action);
            if (index < 0)
            {
                actionsWithNoTreeNode.Add(action);
            }
        }
        return actionsWithNoTreeNode;
    }
}

//Node for an InfosetTree, each node corresponds to an information set for the observing player for a tree,
//and also contains the determinisation that was used when the node was last visited in the tree 
public class InfosetNode
{
    private SeededGameState _refState; //reference state, i.e. one instance of the set of equivalent states for this node
    private Determinisation _visitingDeterminisation; //to store determinisation that was used when this node was last visited in the tree
    public double TotalReward;
    public double VisitCount;
    public double AvailabilityCount;
    public static bool NodeErrorCheck = true;
    
    //create a node using a reference determinisation for information set it encapsulates
    public InfosetNode(Determinisation d)
    {
        _refState = d.state;
        _visitingDeterminisation = d;
        
        //initialise values for UCB calc
        TotalReward = 0.0001;
        VisitCount = 1;
        AvailabilityCount = 1;
    }

    public void SetDeterminisation(Determinisation d)
    {
        //the way the MCTS algorithm is set-up this check should never be needed...
        if (NodeErrorCheck)
        {
            if (!CheckEquivalentState(d.state))
                throw new Exception("Determinisation incompatible with reference state in InfosetNode: SetDeterminisation");
        }

        _visitingDeterminisation = d;
    }

    public Determinisation GetDeterminisation()
    {
        return _visitingDeterminisation;
    }

    //calculate upper confidence bound for trees, bandit algorithm for MCTS tree policy
    public double UCB(double K)
    {
        return TotalReward / VisitCount + K * Math.Sqrt(Math.Log(AvailabilityCount / TotalReward));
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
    
    //get actions available from info set using our visiting determinisation.This is A(d) in pseudo-code. 
    public List<InfosetAction> GetCompatibleActions()
    {
        //generate all possible determinisations from d with the list of possible moves
        List<Determinisation> possibleDeterminisations = new List<Determinisation>();
        foreach (Move move in _visitingDeterminisation.moves)
        {
            var (newSeedGameState, newPossibleMoves) = _visitingDeterminisation.state.ApplyMove(move);
            Determinisation dNew = new Determinisation(newSeedGameState, newPossibleMoves);
            
            possibleDeterminisations.Add(dNew);
        }
        
        //next for each possible new state compatible with our determinisation d we generate an InfosetNode and a
        //corresponding action being careful not to duplicate.
        List<InfosetAction> compatibleActions = new List<InfosetAction>();
        foreach (Determinisation dNew in possibleDeterminisations)
        {
            InfosetNode newNode = new InfosetNode(dNew);
            InfosetAction newAction = new InfosetAction(this, newNode);
            if (!compatibleActions.Contains(newAction))
            {
                compatibleActions.Add(newAction);
            }
        }

        return compatibleActions;
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

