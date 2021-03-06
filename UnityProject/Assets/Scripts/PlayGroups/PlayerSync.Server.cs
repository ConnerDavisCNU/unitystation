using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

public partial class PlayerSync
{
	//Server-only fields, don't concern clients in any way
	private Vector3IntEvent onTileReached = new Vector3IntEvent();
	public Vector3IntEvent OnTileReached() {
		return onTileReached;
	}
	private Vector3IntEvent onUpdateReceived = new Vector3IntEvent();
	public Vector3IntEvent OnUpdateRecieved() {
		return onUpdateReceived;
	}

	/// Current server state. Integer positions.
	private PlayerState serverState;

	/// Serverside lerping state that simulates where players should be on clients at the moment
	private PlayerState serverLerpState;

	private Queue<PlayerAction> serverPendingActions;

	/// Max size of serverside queue, client will be rolled back and punished if it overflows
	private readonly int maxServerQueue = 10;

	private HashSet<PushPull> questionablePushables = new HashSet<PushPull>();

	/// Last direction that player moved in. Currently works more like a true impulse, therefore is zero-able
	private Vector2 serverLastDirection;

	private bool isApplyingSpaceDmg;

	///
	public bool IsWeightlessServer => !playerMove.isGhost && MatrixManager.IsFloatingAt(gameObject, Vector3Int.RoundToInt( serverState.WorldPosition ));
	public bool IsNonStickyServer => !playerMove.isGhost && MatrixManager.IsNonStickyAt(Vector3Int.RoundToInt( serverState.WorldPosition ));
	public bool CanNotSpaceMoveServer => IsWeightlessServer && !IsAroundPushables( serverState );


	/// Whether player is considered to be floating on server
	private bool consideredFloatingServer => serverState.Impulse != Vector2.zero;

	/// Do current and target server positions match?
	private bool ServerPositionsMatch => serverState.WorldPosition == serverLerpState.WorldPosition;

	public override void OnStartServer()
		{
			base.OnStartServer();
			InitServerState();
		}

	///
		[Server]
		private void InitServerState()
		{
			Vector3Int worldPos = Vector3Int.RoundToInt((Vector2) transform.position); //cutting off Z-axis & rounding
			MatrixInfo matrixAtPoint = MatrixManager.AtPoint(worldPos);
			PlayerState state = new PlayerState
			{
				MoveNumber = 0,
				MatrixId = matrixAtPoint.Id,
				WorldPosition = worldPos
			};
			Logger.LogTraceFormat( "{0}: InitServerState for {1} found matrix {2} resulting in\n{3}", Category.Movement,
				PlayerList.Instance.Get( gameObject ).Name, worldPos, matrixAtPoint, state );
			serverLerpState = state;
			serverState = state;

		//Subbing to new matrix rotations
		if (matrixAtPoint.MatrixMove != null)
		{
			matrixAtPoint.MatrixMove.OnRotate.AddListener(OnRotation);
		}
	}

	private PlayerAction lastAddedAction = PlayerAction.None;
	[Command(channel = 0)]
	private void CmdProcessAction(PlayerAction action)
	{
		if ( serverPendingActions.Count > 0 && !lastAddedAction.Equals(PlayerAction.None)
		     && lastAddedAction.isNonPredictive && action.isNonPredictive )
		{
			Logger.Log( $"Ignored {action}: two non-predictive actions in a row!", Category.Movement );
			return;
		}

		//add action to server simulation queue
		serverPendingActions.Enqueue(action);

		lastAddedAction = action;

		//Do not cache the position if the player is a ghost
		//or else new players will sync the deadbody with the last pos
		//of the gost:
		if (playerMove.isGhost)
		{
			return;
		}

		//Rollback pos and punish player if server queue size is more than max size
		if (serverPendingActions.Count > maxServerQueue)
		{
			RollbackPosition();
			Logger.LogWarning( $"{gameObject.name}: Server pending actions overflow! (More than {maxServerQueue})." +
			                   "\nEither server lagged or player is attempting speedhack", Category.Movement );
		}
	}

	/// Push player in direction.
	/// Impulse should be consumed after one tile if indoors,
	/// and last indefinitely (until hit by obstacle) if you pushed someone into deep space
	[Server]
	public bool Push(Vector2Int direction)
	{
		if (direction == Vector2Int.zero)		{
			return false;
		}

		Vector3Int origin = Vector3Int.RoundToInt( (Vector2)serverState.WorldPosition );
		Vector3Int pushGoal = origin + Vector3Int.RoundToInt( (Vector2)direction );

		if ( !MatrixManager.IsPassableAt( origin, pushGoal ) ) {
			return false;
		}

		Logger.Log( $"Server push to {pushGoal}", Category.PushPull );
		ClearQueueServer();
		MatrixInfo newMatrix = MatrixManager.AtPoint( pushGoal );
		//Note the client queue reset
		var newState = new PlayerState {
			MoveNumber = 0,
			Impulse = direction,
			MatrixId = newMatrix.Id,
			WorldPosition = pushGoal,
			ImportantFlightUpdate = true,
			ResetClientQueue = true
		};
		serverLastDirection = direction;
		serverState = newState;
		SyncMatrix();
		NotifyPlayers();

		return true;
	}

	/// Manually set player to a specific world position.
	/// Also clears prediction queues.
	/// <param name="worldPos">The new position to "teleport" player</param>
	[Server]
	public void SetPosition(Vector3 worldPos)
	{
		ClearQueueServer();
		Vector3Int roundedPos = Vector3Int.RoundToInt((Vector2)worldPos); //cutting off z-axis
		MatrixInfo newMatrix = MatrixManager.AtPoint(roundedPos);
		//Note the client queue reset
		var newState = new PlayerState
		{
			MoveNumber = 0,
			MatrixId = newMatrix.Id,
			WorldPosition = roundedPos,
			ResetClientQueue = true
		};
		serverLerpState = newState;
		serverState = newState;
		SyncMatrix();
		NotifyPlayers();
	}

	///	When lerp is finished, inform players of new state
	[Server]
	private void TryNotifyPlayers()
	{
		if (ServerPositionsMatch)
		{
//			Logger.LogTrace( $"{gameObject.name}: PSync Notify success!", Category.Movement );
			serverLerpState = serverState;
			SyncMatrix();
			NotifyPlayers();
			TryUpdateServerTarget();
		}
	}

	/// Register player to matrix from serverState (ParentNetId is a SyncVar)
	[Server]
	private void SyncMatrix()
	{
		registerTile.ParentNetId = MatrixManager.Get(serverState.MatrixId).NetId;
	}

	/// Send current serverState to just one player
	/// <param name="recipient">whom to inform</param>
	/// <param name="noLerp">(for init) tells client to do no lerping when changing pos this time</param>
	[Server]
	public void NotifyPlayer(GameObject recipient, bool noLerp = false)
	{
		serverState.NoLerp = noLerp;
		var msg = PlayerMoveMessage.Send(recipient, gameObject, serverState);
		Logger.LogTraceFormat("Sent {0}", Category.Movement, msg);
	}

	/// Send current serverState to all players
	[Server]
	public void NotifyPlayers() {
		NotifyPlayers(false);
	}

	/// Send current serverState to all players
	[Server]
	public void NotifyPlayers(bool noLerp)
	{
		//Generally not sending mid-flight updates (unless there's a sudden change of course etc.)
		if (!serverState.ImportantFlightUpdate && consideredFloatingServer)
		{
			return;
		}
		serverState.NoLerp = noLerp;
		var msg = PlayerMoveMessage.SendToAll(gameObject, serverState);
//		Logger.LogTraceFormat("SentToAll {0}", Category.Movement, msg);
		//Clearing state flags
		serverState.ImportantFlightUpdate = false;
		serverState.ResetClientQueue = false;
	}

	/// Clears server pending actions queue
	private void ClearQueueServer()
	{
		//			Logger.Log("Server queue wiped!");
		if (serverPendingActions != null && serverPendingActions.Count > 0)
		{
			serverPendingActions.Clear();
		}
	}

	/// Clear all queues and
	/// inform players of true serverState
	[Server]
	private void RollbackPosition()
	{
		foreach ( var questionablePushable in questionablePushables ) {
			Logger.LogWarningFormat( "Notified questionable pushable {0}", Category.PushPull, questionablePushable );
			questionablePushable.NotifyPlayers();
		}
		SetPosition(serverState.WorldPosition);
		questionablePushables.Clear();
	}

	/// Tries to assign next target from queue to serverTargetState if there are any
	/// (In order to start lerping towards it)
	[Server]
	private void TryUpdateServerTarget()
	{
		if (serverPendingActions.Count == 0)
		{
			return;
		}

		if (consideredFloatingServer || CanNotSpaceMoveServer)
		{
			Logger.LogWarning("Server ignored move while player is floating", Category.Movement);
			serverPendingActions.Dequeue();
			return;
		}

		var curState = serverState;
		PlayerState nextState = NextStateServer( curState, serverPendingActions.Dequeue() );

		if ( Equals( curState, nextState ) ) {
			return;
		}

		serverLastDirection = Vector2Int.RoundToInt(nextState.WorldPosition - serverState.WorldPosition);
		serverState = nextState;
		//In case positions already match
		TryNotifyPlayers();
//		Logger.Log($"Server Updated target {serverTargetState}. {serverPendingActions.Count} pending");
		}

	/// NextState that also subscribes player to matrix rotations
	[Server]
	private PlayerState NextStateServer(PlayerState state, PlayerAction action)
	{
		bool isServerBump = !CanMoveThere( state, action );
		bool isClientBump = action.isBump;
		if ( !isClientBump && isServerBump ) {
			Logger.LogWarningFormat( "isBump mismatch, resetting: C={0} S={1}", Category.Movement, isClientBump, isServerBump );
			RollbackPosition();
		}
		if ( isClientBump || isServerBump ) {
			//gotta try pushing things
			BumpInteract( state.WorldPosition, (Vector2) action.Direction() );

			playerSprites.FaceDirection( Orientation.From( action.Direction() ) );
			return state;
		}

		if ( IsNonStickyServer ) {
			PushPull pushable;
			if ( IsAroundPushables( serverState, out pushable ) ) {
				StartCoroutine( InteractSpacePushable( pushable, action.Direction() ) );
			}
			return state;
		}

		if ( action.isNonPredictive )
		{
			Logger.Log( "Ignored action marked as Non-predictive while being indoors", Category.Movement );
			return state;
		}

		bool matrixChangeDetected;
		PlayerState nextState = NextState(state, action, out matrixChangeDetected);

		if (!matrixChangeDetected)
		{
			return nextState;
		}

		//todo: subscribe to current matrix rotations on spawn
		var newMatrix = MatrixManager.Get(nextState.MatrixId);
		Logger.Log($"Matrix will change to {newMatrix}", Category.Movement);
		if (newMatrix.MatrixMove)
		{
			//Subbing to new matrix rotations
			newMatrix.MatrixMove.OnRotate.AddListener(OnRotation);
			//				Logger.Log( $"Registered rotation listener to {newMatrix.MatrixMove}" );
		}

		//Unsubbing from old matrix rotations
		MatrixMove oldMatrixMove = MatrixManager.Get(matrix).MatrixMove;
		if (oldMatrixMove)
		{
			//				Logger.Log( $"Unregistered rotation listener from {oldMatrixMove}" );
			oldMatrixMove.OnRotate.RemoveListener(OnRotation);
		}

		return nextState;
	}

	#region walk interactions

	///Revert client push prediction straight ahead if it's wrong
	[Command(channel = 0)]
	private void CmdValidatePush( GameObject pushable ) {
		var pushPull = pushable.GetComponent<PushPull>();
		if ( pushPull && !playerScript.IsInReach(pushPull.registerTile.WorldPosition) ) {
			questionablePushables.Add( pushPull );
			Logger.LogWarningFormat( "Added questionable {0}", Category.PushPull, pushPull );
		}
	}

	private void BumpInteract(Vector3 currentPosition, Vector3 direction) {
			StartCoroutine( TryInteract( currentPosition, direction ) );
	}

	private IEnumerator TryInteract( Vector3 currentPosition, Vector3 direction ) {
		var worldPos = Vector3Int.RoundToInt(currentPosition);
		var worldTarget = Vector3Int.RoundToInt(currentPosition + direction);

		InteractDoor(worldPos, worldTarget);

//		Logger.LogTraceFormat( "{0} Interacting {1}->{2}, server={3}", Category.Movement, Time.unscaledTime*1000, worldPos, worldTarget, isServer );
		InteractPushable( worldTarget, direction );

		yield return YieldHelper.DeciSecond;
	}

	private IEnumerator InteractSpacePushable( PushPull pushable, Vector2 direction, bool isRecursive = false, int i = 0 ) {
		Logger.LogTraceFormat( (isRecursive ? "Recursive " : "") + "Trying to space push {0}", Category.PushPull, pushable.gameObject );
		
		if ( !isRecursive )
		{
			i = CalculateRequiredPushes( serverState.WorldPosition, pushable.registerTile.WorldPosition, direction );
			Logger.LogFormat( "Calculated {0} required pushes", Category.Movement, i );
		}

		if ( i <= 0 ) yield break;

		Vector2 counterDirection = Vector2.zero - direction;
		
		pushable.QueuePush( Vector2Int.RoundToInt( counterDirection ) );
		i--;
		Logger.LogFormat( "Queued obstacle push. {0} pushes left", Category.Movement, i );
		
		if ( i <= 0 ) yield break;
		
		pushPull.QueuePush( Vector2Int.RoundToInt( direction ) );
		i--;
		Logger.LogFormat( "Queued player push. {0} pushes left", Category.Movement, i );
		

		if ( i > 0 ) 
		{ 
			StartCoroutine( InteractSpacePushable( pushable, direction, true, i ) );
		}

		yield return null;
	}

	private int CalculateRequiredPushes( Vector3 playerPos, Vector3Int pushablePos, Vector2 impulse )
	{
		return 5;
	}

	/// <param name="worldTile">Tile you're interacting with</param>
	/// <param name="direction">Direction you're pushing</param>
	private void InteractPushable( Vector3Int worldTile, Vector3 direction ) {
		if ( IsNonStickyServer ) {
			return;
		}
		// Is the object pushable (iterate through all of the objects at the position):
		PushPull[] pushPulls = MatrixManager.GetAt<PushPull>( worldTile ).ToArray();
		for ( int i = 0; i < pushPulls.Length; i++ ) {
			var pushPull = pushPulls[i];
			if ( pushPull && pushPull.gameObject != gameObject && pushPull.IsSolid ) {
	//					Logger.LogTraceFormat( "Trying to push {0} when walking {1}->{2}", Category.PushPull, pushPulls[i].gameObject, worldPos, worldTarget );
				pushPull.TryPush( worldTile, Vector2Int.RoundToInt( direction ) );
				break;
			}
		}
	}

	private void InteractDoor(Vector3Int currentPos, Vector3Int targetPos)
	{
		// Make sure there is a door controller
		DoorTrigger door = MatrixManager.Instance.GetFirst<DoorTrigger>(targetPos);

		if (!door)
		{
			door = MatrixManager.Instance.GetFirst<DoorTrigger>(Vector3Int.RoundToInt(currentPos));

			if (door)
			{
				RegisterDoor registerDoor = door.GetComponent<RegisterDoor>();
				Vector3Int localPos = MatrixManager.Instance.WorldToLocalInt(targetPos, matrix);

				if (registerDoor.IsPassable(localPos))
				{
					door = null;
				}
			}
		}

		// Attempt to open door
		if (door != null)
		{
			door.Interact(gameObject, TransformState.HiddenPos);
		}
	}

		#endregion

	[Server]
	private void OnRotation(Orientation from, Orientation to)
	{
		//fixme: doesn't seem to change orientation for clients from their point of view
		playerSprites.ChangePlayerDirection(Orientation.DegreeBetween(from, to));
	}

	/// Lerping and ensuring server authority for space walk
	[Server]
	private void CheckMovementServer()
	{
		//Space walk checks
		if ( IsNonStickyServer )
		{
			if (serverState.Impulse == Vector2.zero && serverLastDirection != Vector2.zero)
			{
				//server initiated space dive.
				serverState.Impulse = serverLastDirection;
				serverState.ImportantFlightUpdate = true;
				serverState.ResetClientQueue = true;
			}

			//Perpetual floating sim
			if (ServerPositionsMatch)
			{
				if ( serverState.ImportantFlightUpdate ) {
					NotifyPlayers();
				} else {
					//Extending prediction by one tile if player's transform reaches previously set goal
					Vector3Int newGoal = Vector3Int.RoundToInt(serverState.Position + (Vector3)serverState.Impulse);
					serverState.Position = newGoal;
					ClearQueueServer();
				}
			}
		}

		if ( consideredFloatingServer && !IsWeightlessServer ) {
			Stop();
		}

		CheckSpaceDamage();
	}

	public void Stop() {
		if ( consideredFloatingServer ) {
			PushPull spaceObjToGrab;
			if ( IsAroundPushables( serverState, out spaceObjToGrab ) && spaceObjToGrab.IsSolid ) {
				//some hacks to avoid space closets stopping out of player's reach
				var cnt = spaceObjToGrab.GetComponent<CustomNetTransform>();
				if ( cnt && cnt.IsFloatingServer && Vector2Int.RoundToInt(cnt.ServerState.Impulse) == Vector2Int.RoundToInt(serverState.Impulse) )
				{
					Logger.LogTraceFormat( "Caught {0} at {1} (registered at {2})", Category.Movement, spaceObjToGrab.gameObject.name,
							( Vector2 ) cnt.ServerState.WorldPosition, ( Vector2 ) ( Vector3 ) spaceObjToGrab.registerTile.WorldPosition );
					cnt.SetPosition( spaceObjToGrab.registerTile.WorldPosition );
					spaceObjToGrab.Stop();
				}
			}
			//removing lastDirection when we hit an obstacle in space
			serverLastDirection = Vector2.zero;

			//finish floating. players will be notified as soon as serverState catches up
			serverState.Impulse = Vector2.zero;
			serverState.ResetClientQueue = true;

			//Stopping spacewalk increases move number
			serverState.MoveNumber++;

			//Notify if position stayed the same
			NotifyPlayers();
		}
	}

	private void ServerLerp() {
		//Lerp on server if it's worth lerping
		//and inform players if serverState reached targetState afterwards
		Vector3 targetPos = serverState.WorldPosition;
		serverLerpState.WorldPosition =
			Vector3.MoveTowards( serverLerpState.WorldPosition, targetPos,
								 playerMove.speed * Time.deltaTime * serverLerpState.WorldPosition.SpeedTo(targetPos) );
		//failsafe
		var distance = Vector3.Distance( serverLerpState.WorldPosition, targetPos );
		if ( distance > 1.5 ) {
			Logger.LogWarning( $"Dist {distance} > 1:{serverLerpState}\n" +
			                   $"Target    :{serverState}", Category.Movement );
			serverLerpState.WorldPosition = targetPos;
		}
		if ( serverLerpState.WorldPosition == targetPos ) {
			onTileReached.Invoke( Vector3Int.RoundToInt(targetPos) );
		}
		TryNotifyPlayers();
	}

	/// Checking whether player should suffocate
	[Server]
	private void CheckSpaceDamage()
	{
		if (MatrixManager.IsSpaceAt(Vector3Int.RoundToInt(serverState.WorldPosition))
			&& !healthBehaviorScript.IsDead && !isApplyingSpaceDmg)
		{
			// Hurting people in space even if they are next to the wall
			if (!IsEvaCompatible())
			{
				StartCoroutine(ApplyTempSpaceDamage());
				isApplyingSpaceDmg = true;
			}
		}
	}

	// TODO: Remove this when atmos is implemented
	// This prevents players drifting into space indefinitely
	private IEnumerator ApplyTempSpaceDamage()
	{
		yield return new WaitForSeconds(1f);
		healthBehaviorScript.ApplyDamage(null, 5, DamageType.OXY, BodyPartType.HEAD);
		isApplyingSpaceDmg = false;
	}

	//FIXME: The new PlayerInventory(wip) component will handle all this a lot better
	//FIXME: adding temp caches for the time being:
	private GameObject headObjCache;
	private GameObject suitObjCache;
	private ItemAttributes headItemAtt;
	private ItemAttributes suitItemAtt;

	//Temp solution (move to playerinventory when its completed):
	private bool IsEvaCompatible()
	{
		var headItem = playerScript.playerNetworkActions.Inventory["head"].Item;
		var suitItem = playerScript.playerNetworkActions.Inventory["suit"].Item;
		if ( headItem == null || suitItem == null )
		{
			return false;
		}

		if (headObjCache != headItem)
		{
			headObjCache = headItem;
			if (headObjCache != null)
				headItemAtt = headObjCache.GetComponent<ItemAttributes>();
		}
		if (suitObjCache != suitItem)
		{
			suitObjCache = suitItem;
			if (suitObjCache != null)
				suitItemAtt = suitObjCache.GetComponent<ItemAttributes>();
		}

		return headItemAtt.evaCapable && suitItemAtt.evaCapable;
	}
}
