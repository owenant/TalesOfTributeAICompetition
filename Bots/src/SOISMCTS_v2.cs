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
    private static int _depthCounter = 0; //total tree depth explored across all moves
    private static int _widthFirstLayerCounter = 0; //number of nodes in first layer of tree
    private static int _widthSecondLayerCounter = 0; //number of nodes in second layer of tree
    
    //counter for number of times play method is called
    private int playMethodCallCount = 0;
    
    //parameters for MCTS
    private readonly double K = 0.7; //explore vs exploit parameter for tree policy
    private readonly int maxSimulationDepth = 0; //only explore current player, if we change this to one or greater
    //we may need to update UCB and heuritsic to reflect eneemy player turns
    
    //parameters for heuristicfrom MCTSBot.cs
    private int _patronFavour = 50;
    private int _patronNeutral = 10;
    private int _patronUnfavour = -50;
    private int _coinsValue = 1;
    private int _powerValue = 40;
    private int _prestigeValue = 50;
    private int _agentOnBoardValue = 30;
    private int _hpValue = 3;
    private int _opponentAgentsPenaltyValue = 40;
    private int _potentialComboValue = 3;
    private int _cardValue = 10;
    private int _penaltyForHighTierInTavern = 2;
    private int _numberOfDrawsValue = 10;
    private int _enemyPotentialComboPenalty = 1;
    private int _heuristicMax = 40000; //160
    private int _heuristicMin = -10000;//00
    //int heuristicMax = 160;
    //int heuristicMin = 0;

    public static bool errorcheck = false;
    
    private void PrepareForGame()
    { 
        //if any agent set-up needed it can be done here
        
        //seed random number generator
        long seed = DateTime.Now.Ticks;
        rng = new(123);  
        //rng = new((ulong)seed); 
        
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
        }

        //Initialise a root node
        PlayerEnum observingPlayer = gameState.CurrentPlayer.PlayerID; //observing player for information sets in tree
        SeededGameState s = gameState.ToSeededGameState((ulong) rng.Next());
        Determinisation d = new Determinisation(s, possibleMoves); //note that all possible moves are compatible with all seeds at the root
        InfosetNode root = new InfosetNode(null, null, d, observingPlayer);
        
        //in InfosetMCTS each iteration of the loop starts with a new determinisation we use to explore the same tree
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
            int maxDepthForThisMove = 0;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            while (timer.Elapsed < _timeForMoveComputation)
            //int maxIterations = 50;
            //for(int i = 0; i < maxIterations; i++)
            {
                //if (_moveCounter == 17)
                //{
                //    int j = 0;
                //}
                //create a random determinisation - commented out for comparison against vanilla MCTS
                //s = gameState.ToSeededGameState((ulong) rng.Next());
                //d = new Determinisation(s, possibleMoves); //not possible moves are compatible with all seeds at the root
                //and set as determinisation to use for this iteration
                //root.SetDeterminisationAndParentMove(d, null);
                
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
                double payoutFromExpandedNode = Simulate((Determinisation) expandedNode.GetDeterminisation(), maxSimulationDepth);

                //next we complete the backpropagation step
                BackPropagation(payoutFromExpandedNode, pathThroughTree);

                _simsCounter += 1;
                //noIterations += 1;

                maxDepthForThisMove = Math.Max(maxDepthForThisMove, pathThroughTree.Count);
            }
            _usedTimeInTurn += _timeForMoveComputation;
            
            //increase depth counter
            _depthCounter += maxDepthForThisMove;
            
            //increase width counter
            _widthFirstLayerCounter += root.Children.Count;

            // if (_turnCounter == 1 && _moveCounter == 3)
            // {
            //     InfosetNode node0 = root.Children[0];
            //     InfosetNode node1 = root.Children[1];
            //     bool test = node0.CheckEquivalentState(node1.GetDeterminisation().GetState());
            //     int i = 0;
            // }

            //finally we return the move from the root node that leads to a node with the maximum visit count
            chosenMove = chooseBestMove(root);

            //if (chosenMove is null)
            //{
            //    int i = 0;
            //}
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
        //Each successive node is chosen using our tree policy until a node is reached such that some action from that node leads to
        //an information set that is not currently in the tree or the node v is terminal
        InfosetNode bestNode = startNode;
        List<InfosetNode> cvd = startNode.GetCompatibleChildrenInTree();
        List<Move> uvd = startNode.GetMovesWithNoChildren();
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
            cvd = bestNode.GetCompatibleChildrenInTree();
            uvd = bestNode.GetMovesWithNoChildren();
            pathThroughTree.Add(bestNode);
        }
        return pathThroughTree;
    }

    //uvd is the set of actions from 
    private InfosetNode Expand(InfosetNode node)
    {
        //choose a move at random from our list of moves that do not have nodes in the tree
        //and add child node to tree
        List<Move> uvd = node.GetMovesWithNoChildren();
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
            //double playoutScore = playoutHeuristic(bestd.GetState());
            //use MCTSBOt heuristic
            double playoutScore = Heuristic(bestd.GetState());
            double firstPlayoutScore = playoutScore;
            bestPlayoutScore = firstPlayoutScore;

            bool allequal = true;
            for (var i = 1; i < possibleDeterminisations.Count; i++) //ignore the first move in this loop 
            {
                playoutScore = Heuristic(possibleDeterminisations[i].GetState());
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

            //if all nodes have the same playout score chose one at random
            if (allequal)
            {
                bestd = possibleDeterminisations.PickRandom(rng);
            }

            d = bestd;
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
            List<InfosetNode> cvd = node.GetCompatibleChildrenInTree();
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
        //as it is the observing player's turn to go. Also the move that was used to last to go from the root
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
    
    //Heuristic 'borrowed' from MCTSBot.cs
    public double Heuristic(SeededGameState gameState)
    {
        int finalValue = 0;
        int enemyPatronFavour = 0;
        foreach (KeyValuePair<PatronId, PlayerEnum> entry in gameState.PatronStates.All)
        {
            if (entry.Key == PatronId.TREASURY)
            {
                continue;
            }
            if (entry.Value == gameState.CurrentPlayer.PlayerID)
            {
                finalValue += _patronFavour;
            }
            else if (entry.Value == PlayerEnum.NO_PLAYER_SELECTED)
            {
                finalValue += _patronNeutral;
            }
            else
            {
                finalValue += _patronUnfavour;
                enemyPatronFavour += 1;
            }
        }
        if (enemyPatronFavour >= 2)
        {
            finalValue -= 100;
        }

        finalValue += gameState.CurrentPlayer.Power * _powerValue;
        finalValue += gameState.CurrentPlayer.Prestige * _prestigeValue;
        //finalValue += gameState.CurrentPlayer.Coins * _coinsValue;

        if (gameState.CurrentPlayer.Prestige < 30)
        {
            TierEnum tier = TierEnum.UNKNOWN;

            foreach (SerializedAgent agent in gameState.CurrentPlayer.Agents)
            {
                tier = CardTierList.GetCardTier(agent.RepresentingCard.Name);
                finalValue += _agentOnBoardValue * (int)tier + agent.CurrentHp * _hpValue;
            }

            foreach (SerializedAgent agent in gameState.EnemyPlayer.Agents)
            {
                tier = CardTierList.GetCardTier(agent.RepresentingCard.Name);
                finalValue -= _agentOnBoardValue * (int)tier + agent.CurrentHp * _hpValue + _opponentAgentsPenaltyValue;
            }

            List<UniqueCard> allCards = gameState.CurrentPlayer.Hand.Concat(gameState.CurrentPlayer.Played.Concat(gameState.CurrentPlayer.CooldownPile.Concat(gameState.CurrentPlayer.DrawPile))).ToList();
            Dictionary<PatronId, int> potentialComboNumber = new Dictionary<PatronId, int>();
            List<UniqueCard> allCardsEnemy = gameState.EnemyPlayer.Hand.Concat(gameState.EnemyPlayer.DrawPile).Concat(gameState.EnemyPlayer.Played.Concat(gameState.EnemyPlayer.CooldownPile)).ToList();
            Dictionary<PatronId, int> potentialComboNumberEnemy = new Dictionary<PatronId, int>();

            foreach (UniqueCard card in allCards)
            {
                tier = CardTierList.GetCardTier(card.Name);
                finalValue += (int)tier * _cardValue;
                if (card.Deck != PatronId.TREASURY)
                {
                    if (potentialComboNumber.ContainsKey(card.Deck))
                    {
                        potentialComboNumber[card.Deck] += 1;
                    }
                    else
                    {
                        potentialComboNumber[card.Deck] = 1;
                    }
                }
            }

            foreach (UniqueCard card in allCardsEnemy)
            {
                if (card.Deck != PatronId.TREASURY)
                {
                    if (potentialComboNumberEnemy.ContainsKey(card.Deck))
                    {
                        potentialComboNumberEnemy[card.Deck] += 1;
                    }
                    else
                    {
                        potentialComboNumberEnemy[card.Deck] = 1;
                    }
                }
            }

            foreach (KeyValuePair<PatronId, int> entry in potentialComboNumber)
            {
                finalValue += (int)Math.Pow(entry.Value, _potentialComboValue);
            }

            foreach (Card card in gameState.TavernAvailableCards)
            {
                tier = CardTierList.GetCardTier(card.Name);
                finalValue -= _penaltyForHighTierInTavern * (int)tier;
                /*
                if (potentialComboNumberEnemy.ContainsKey(card.Deck) && (potentialComboNumberEnemy[card.Deck]>4) && (tier > TierEnum.B)){
                    finalValue -= enemyPotentialComboPenalty*(int)tier;
                }
                */
            }

        }

        //int finalValue = gameState.CurrentPlayer.Power + gameState.CurrentPlayer.Prestige;
        double normalizedValue = NormalizeHeuristic(finalValue);

        return normalizedValue;
    }

    private double NormalizeHeuristic(int value)
    {
        double normalizedValue = ((double)value - (double)_heuristicMin) / ((double)_heuristicMax - (double)_heuristicMin);

        if (normalizedValue < 0)
        {
            return 0.0;
        }

        return normalizedValue;
    }

    public override void GameEnd(EndGameState state, FullGameState? finalBoardState)
    {
        double avgMovesPerTurn = _moveCounter/ (1.0 * _turnCounter);
        double avgSimsPerMove = _simsCounter / (1.0 * _moveCounter);
        double avgDepthPerMove = _depthCounter/ (1.0 * _moveCounter);
        double avgWidthFirstLayerPerMove = _widthFirstLayerCounter/ (1.0 * _moveCounter);
        
        string message = "Game count: " + _gameCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Turn Counter: " + _turnCounter.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of moves per turn: " + avgMovesPerTurn.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average number of simulations per move: " + avgSimsPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average tree depth searched per move: " + avgDepthPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Average width of first layer of tree for each move: " + avgWidthFirstLayerPerMove.ToString();
        log.Log(finalBoardState.CurrentPlayer.PlayerID, message);
        message = "Winner: " + state.Winner.ToString();
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
    public int VisitCount;
    public int AvailabilityCount;
    public PlayerEnum ObservingPlayer; //observing player is assumed to be the player to play next at the root node
    
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
        
        this.SetDeterminisationAndParentMove(d, moveFromParent);
    }

    //this method updates the current determinisation and parent move for a node, and then 
    //for each of the nodes furtehr down the tree sets to null the current determinisation
    //and parent move, so that they get recalculated when calling GetChildrenInTreeAndMovesNotInTree
    public void SetDeterminisationAndParentMove(Determinisation? d, Move? fromParent)
    {
        _currentDeterminisation = d;
        _currentMoveFromParent = fromParent;

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

            // if (move.Command == CommandEnum.END_TURN)
            // {
            //     int i = 0;
            // }
            //find if new state is in tree or not
            bool found = false;
            foreach (InfosetNode child in Children)
            {
                if (child.CheckEquivalentState(newState, move))
                {
                    //found child node that represents an information set containing equivalent states
                    found = true;
                    child.SetDeterminisationAndParentMove(new Determinisation(newState, newMoves), move);
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
    
    public InfosetNode CreateChild(Move parentMove, Determinisation newd)
    {
        //TODO::this needs to add an information set node that is appropriate
        //for the observing player. Dont think this matters, as we are using a seeded game state anyway......
        InfosetNode childNode = new InfosetNode(this, parentMove, newd, this.ObservingPlayer);
        Children.Add(childNode);
        
        //also need to add to list of compatible children as this funciton is called when expanding the tree 
        //and hence this child node is be definition compatibel with it's parent
        this._compatibleChildrenInTree.Add(childNode);
        
        //also need to remove this parent move form the list of moves not in the tree
        this._currentMovesWithNoChildren.Remove(parentMove);

        return childNode;
    }
    
    //check if a state is part of the equivalence class for this node
    public bool CheckEquivalentState(SeededGameState state, Move parentMove)
    {
        //to check whether our states are equivalent we need to identify information visible to the observing player
        //and ensure that is the same in both cases.
        //if (!sameVisibleInfo(state))
        //    return false;
        
        //for comparison against standard MCTS
        if (!(this.EqualsMove(_currentMoveFromParent, parentMove)))
            return false;
        
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
    
    // Override Equals method to define equality (this allows search on lists of InfosetNodes)
    public override bool Equals(object obj)
    {
        InfosetNode node = (InfosetNode)obj;
        
        return CheckEquivalentState(node._refState, node._currentMoveFromParent);
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

    public bool EqualsOverride<T>(T item1, T item2)
    {
        if ((item1 is SerializedAgent) && (item2 is SerializedAgent))
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
        if (card1.GetType() != card2.GetType())
            return false;

        return true;
    }
    
    //check for same move
    private bool EqualsMove(Move move1, Move move2)
    {
        if (move1.Command != move2.Command)
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