﻿using System;
using System.Collections.Generic;
using System.Linq;
using Sabine.Shared.Const;
using Sabine.Shared.Util;
using Sabine.Shared.World;
using Sabine.Zone.Network;
using Sabine.Zone.World.Entities.CharacterComponents;
using Sabine.Zone.World.Maps;
using Shared.Const;
using Yggdrasil.Util;

namespace Sabine.Zone.World.Entities
{
	/// <summary>
	/// Represents a player character.
	/// </summary>
	public class PlayerCharacter : ICharacter, IUpdateable
	{
		private readonly object _visibilityUpdateSyncLock = new object();
		private readonly HashSet<int> _visibleEntities = new HashSet<int>();

		/// <summary>
		/// Gets or sets the connection that controls this player.
		/// </summary>
		public ZoneConnection Connection { get; set; } = new DummyConnection();

		/// <summary>
		/// Returns the character's variable container.
		/// </summary>
		public VariableContainer Vars { get; } = new VariableContainer();

		/// <summary>
		/// Returns this character's username.
		/// </summary>
		public string Username => this.Connection.Account.Username;

		/// <summary>
		/// Returns this character's session id.
		/// </summary>
		public int SessionId => this.Connection.Account.SessionId;

		/// <summary>
		/// Returns the character's handle.
		/// </summary>
		public int Handle => this.SessionId;

		/// <summary>
		/// Gets or sets this character's id.
		/// </summary>
		public int Id { get; set; }

		/// <summary>
		/// Gets or sets this character's name.
		/// </summary>
		public string Name { get; set; }

		/// <summary>
		/// Returns this character's/account's sex.
		/// </summary>
		public Sex Sex => this.Connection.Account.Sex;

		/// <summary>
		/// Gets or sets this character's job.
		/// </summary>
		public JobId JobId { get; set; } = JobId.Novice;

		/// <summary>
		/// Returns the character's speed from its parameters.
		/// </summary>
		public int Speed => this.Parameters.Speed;

		/// <summary>
		/// Returns the character's class id, which is equal to its
		/// current job id.
		/// </summary>
		public int ClassId => (int)this.JobId;

		/// <summary>
		/// Gets or sets the character's hair.
		/// </summary>
		public int HairId { get; set; }

		/// <summary>
		/// Gets or sets the character's weapon.
		/// </summary>
		public int WeaponId { get; set; }

		/// <summary>
		/// Returns a reference to the character's parameters/stats.
		/// </summary>
		public Parameters Parameters { get; }

		/// <summary>
		/// Returns a reference to the character's inventory.
		/// </summary>
		public Inventory Inventory { get; }

		/// <summary>
		/// Gets or sets the character's state.
		/// </summary>
		public CharacterState State { get; set; }

		/// <summary>
		/// Gets or sets the id of the map the character is on.
		/// </summary>
		public int MapId { get; set; } = 100036;

		/// <summary>
		/// Gets or sets the character's current position.
		/// </summary>
		public Position Position { get; set; } = new Position(99, 81);

		/// <summary>
		/// Gets or sets the direction the character is looking in.
		/// </summary>
		public Direction Direction { get; set; } = Direction.North;

		/// <summary>
		/// Returns true if character is warping to a new location.
		/// </summary>
		public bool IsWarping { get; private set; }

		/// <summary>
		/// Returns the position the character is warping towards while
		/// IsWarping is true.
		/// </summary>
		public Location WarpLocation { get; private set; }

		/// <summary>
		/// Returns a reference to the map the character is currently on.
		/// </summary>
		public Map Map
		{
			get => _map;
			set => _map = value ?? Map.Limbo;
		}
		private Map _map = Map.Limbo;

		/// <summary>
		/// Gets or sets whether the character is currently observing
		/// its surroundings, actively updating the visible entities.
		/// </summary>
		public bool IsObserving { get; protected set; }

		/// <summary>
		/// Creates a new character.
		/// </summary>
		public PlayerCharacter()
		{
			this.Parameters = new Parameters(this);
			this.Inventory = new Inventory(this);
		}

		/// <summary>
		/// Sends a server message to the character's client that is
		/// displayed in the chat log.
		/// </summary>
		/// <param name="format"></param>
		/// <param name="args"></param>
		public void ServerMessage(string format, params object[] args)
		{
			if (args.Length > 0)
				format = string.Format(format, args);

			var message = string.Format(Localization.Get("[Server] : {0}"), format);

			Send.ZC_NOTIFY_CHAT(this, 0, message);
		}

		/// <summary>
		/// Warps character to given location.
		/// </summary>
		/// <param name="mapId"></param>
		/// <param name="pos"></param>
		public void Warp(int mapId, Position pos)
			=> this.Warp(new Location(mapId, pos));

		/// <summary>
		/// Warps character to given location.
		/// </summary>
		/// <param name="location"></param>
		public void Warp(Location location)
		{
			if (!ZoneServer.Instance.World.Maps.TryGet(location.MapId, out var map))
				throw new ArgumentException($"Map '{location.MapId}' not found.");

			this.IsWarping = true;
			this.WarpLocation = location;

			this.StopObserving();

			Send.ZC_NPCACK_MAPMOVE(this, map.StringId, location.Position);
		}

		/// <summary>
		/// Finalizes a warp, actually moving the character to the
		/// new location.
		/// </summary>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="ArgumentException"></exception>
		public void FinalizeWarp()
		{
			if (!this.IsWarping)
				throw new InvalidOperationException("No warp in process that could be finalized.");

			if (!ZoneServer.Instance.World.Maps.TryGet(this.WarpLocation.MapId, out var map))
				throw new ArgumentException($"Map '{this.WarpLocation.MapId}' not found.");

			this.Map.RemoveCharacter(this);
			this.SetLocation(this.WarpLocation);
			map.AddCharacter(this);

			this.IsWarping = false;
			this.StartObserving();
		}

		/// <summary>
		/// Sets character's map id and position.
		/// </summary>
		/// <param name="location"></param>
		public void SetLocation(Location location)
		{
			this.MapId = location.MapId;
			this.Position = location.Position;
		}

		/// <summary>
		/// Returns the character's location.
		/// </summary>
		/// <returns></returns>
		public Location GetLocation()
			=> new Location(this.MapId, this.Position);

		/// <summary>
		/// Makes character sit down.
		/// </summary>
		public void SitDown()
		{
			if (this.State != CharacterState.Standing)
				return;

			this.State = CharacterState.Sitting;
			Send.ZC_NOTIFY_ACT(this, this.Handle, 0, 0, 0, ActionType.SitDown);
		}

		/// <summary>
		/// Makes character stand up.
		/// </summary>
		public void StandUp()
		{
			if (this.State != CharacterState.Sitting)
				return;

			this.State = CharacterState.Standing;
			Send.ZC_NOTIFY_ACT(this, this.Handle, 0, 0, 0, ActionType.StandUp);
		}

		/// <summary>
		/// Stops character's movement.
		/// </summary>
		public void StopMove()
		{
			var pos = this.Position; // TODO: Calculate current position
			Send.ZC_STOPMOVE(this, pos);
		}

		/// <summary>
		/// Updates character.
		/// </summary>
		/// <param name="elapsed"></param>
		public void Update(TimeSpan elapsed)
		{
			this.UpdateVisibility();
		}

		/// <summary>
		/// Starts updating of visible entities. A visibility update is
		/// executed when this method is called.
		/// </summary>
		public void StartObserving()
		{
			lock (_visibilityUpdateSyncLock)
			{
				if (this.IsObserving)
					return;

				this.IsObserving = true;
				this.UpdateVisibility();
			}
		}

		/// <summary>
		/// Stops updating of visible entities. A visibility update is
		/// executed when this method is called.
		/// </summary>
		public void StopObserving()
		{
			lock (_visibilityUpdateSyncLock)
			{
				if (!this.IsObserving)
					return;

				this.IsObserving = false;
				this.RemoveVisibleEntities();
			}
		}

		/// <summary>
		/// Updates visible entities around character.
		/// </summary>
		public void UpdateVisibility()
		{
			if (!this.IsObserving)
				return;

			lock (_visibilityUpdateSyncLock)
			{
				var visibleEntities = this.Map.GetVisibleEntities(this);

				var appeared = visibleEntities.Where(a => !_visibleEntities.Contains(a.Handle));
				var disappeared = _visibleEntities.Where(a => !visibleEntities.Exists(b => b.Handle == a));

				foreach (var entity in appeared)
				{
					if (entity == this)
						continue;

					switch (entity)
					{
						case ICharacter character: Send.ZC_NOTIFY_STANDENTRY(this, character); break;
						case Item item: Send.ZC_ITEM_ENTRY(this, item); break;
					}
				}

				foreach (var handle in disappeared)
				{
					if (handle == this.Handle)
						continue;

					if (handle < 0x6000_0000)
						Send.ZC_NOTIFY_VANISH(this, handle, DisappearType.Vanish);
					else
						Send.ZC_ITEM_DISAPPEAR(this, handle);
				}

				// To remember the visible entities for the next run we store
				// their ids. There might be some cases where it would be
				// useful to have the actual references, but we can still
				// get those if we need to, and this way there's no chance
				// for any memory leaks because we're storing objects
				// that reference each other.

				_visibleEntities.Clear();
				_visibleEntities.UnionWith(visibleEntities.Select(a => a.Handle));
			}
		}

		/// <summary>
		/// Adds handle to the character's visible entities.
		/// </summary>
		/// <param name="handle"></param>
		public void MarkVisible(int handle)
		{
			lock (_visibilityUpdateSyncLock)
				_visibleEntities.Add(handle);
		}

		/// <summary>
		/// Clears the list of visible entities and updates the client.
		/// </summary>
		private void RemoveVisibleEntities()
		{
			lock (_visibilityUpdateSyncLock)
			{
				foreach (var handle in _visibleEntities)
					Send.ZC_NOTIFY_VANISH(this, handle, DisappearType.Vanish);

				_visibleEntities.Clear();
			}
		}

		/// <summary>
		/// Returns true if the character is able to equip the given item,
		/// based on its level and job requirements.
		/// </summary>
		/// <param name="item"></param>
		/// <returns></returns>
		public bool CanEquip(Item item)
		{
			if (item.Data.SexAllowed != Sex.Any && item.Data.SexAllowed != this.Sex)
				return false;

			// There's no mention of required levels in the GameFAQs
			// alpha guide. Did they not exist? Maybe finding enough
			// Zeny was challenge enough?
			if (this.Parameters.BaseLevel < item.Data.RequiredLevel)
				return false;

			if (!this.JobId.Matches(item.Data.JobsAllowed))
				return false;

			return true;
		}

		/// <summary>
		/// Changes the character's look and updates the client.
		/// </summary>
		/// <param name="type"></param>
		/// <param name="lookId"></param>
		public void ChangeLook(SpriteType type, int lookId)
		{
			switch (type)
			{
				case SpriteType.Hair: this.HairId = lookId; break;
				case SpriteType.Weapon: this.WeaponId = lookId; break;
				default:
					throw new ArgumentException($"Unsupported type '{type}'.");
			}

			Send.ZC_SPRITE_CHANGE(this, type, lookId);
		}

		/// <summary>
		/// Changes character's job and updates the client.
		/// </summary>
		/// <param name="jobId"></param>
		public void ChangeJob(JobId jobId)
		{
			this.JobId = jobId;
			Send.ZC_SPRITE_CHANGE(this, SpriteType.Class, (int)jobId);

			this.Inventory.RefreshClient();
		}

		/// <summary>
		/// Drops item in range of the character.
		/// </summary>
		/// <param name="item"></param>
		public void Drop(Item item)
		{
			var rnd = RandomProvider.Get();
			var pos = this.Position;

			pos.X += rnd.Next(-1, 2);
			pos.Y += rnd.Next(-1, 2);

			item.MapId = this.MapId;
			item.Position = pos;

			this.Map.AddItem(item);
		}

		/// <summary>
		/// Returns the character's current attack range, based on its
		/// state and equipped items.
		/// </summary>
		/// <returns></returns>
		public int GetAttackRange()
		{
			// Range is 3 for normal attacks and 16 for ranged
			// in the alpha client. This is hardcoded, based on
			// the type of the item that was equipped.

			if (this.Inventory.RightHand?.Type == ItemType.RangedWeapon)
				return 16;

			return 3;
		}
	}
}
