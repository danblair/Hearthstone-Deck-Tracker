﻿#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using HearthDb.Enums;
using HearthMirror;
using HearthMirror.Objects;
using Hearthstone_Deck_Tracker.Enums;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Hearthstone.Secrets;
using Hearthstone_Deck_Tracker.Replay;
using Hearthstone_Deck_Tracker.Stats;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Hearthstone_Deck_Tracker.Windows;
using MahApps.Metro.Controls.Dialogs;

#endregion

namespace Hearthstone_Deck_Tracker.Hearthstone
{
	public class GameV2 : IGame
	{
		public readonly List<long> IgnoredArenaDecks = new List<long>();
		private GameMode _currentGameMode = GameMode.None;
		private Mode _currentMode;
		private BrawlInfo _brawlInfo;

		public GameV2()
		{
			Player = new Player(this, true);
			Opponent = new Player(this, false);
			IsInMenu = true;
			SecretsManager = new SecretsManager(this);
			Reset();
		}

		public List<string> PowerLog { get; } = new List<string>();
		public Deck IgnoreIncorrectDeck { get; set; }
		public GameTime GameTime { get; } = new GameTime();
		public bool IsMinionInPlay => Entities.FirstOrDefault(x => (x.Value.IsInPlay && x.Value.IsMinion)).Value != null;

		public bool IsOpponentMinionInPlay
			=> Entities.FirstOrDefault(x => (x.Value.IsInPlay && x.Value.IsMinion && x.Value.IsControlledBy(Opponent.Id))).Value != null;

		public int OpponentMinionCount => Entities.Count(x => (x.Value.IsInPlay && x.Value.IsMinion && x.Value.IsControlledBy(Opponent.Id)));
		public int PlayerMinionCount => Entities.Count(x => (x.Value.IsInPlay && x.Value.IsMinion && x.Value.IsControlledBy(Player.Id)));
		public int OpponentHandCount => Entities.Count(x => x.Value.IsInHand && x.Value.IsControlledBy(Opponent.Id));

		public Player Player { get; set; }
		public Player Opponent { get; set; }
		public bool IsInMenu { get; set; }
		public bool IsUsingPremade { get; set; }
		public bool IsRunning { get; set; }
		public Region CurrentRegion { get; set; }
		public GameStats CurrentGameStats { get; set; }
		public HearthMirror.Objects.Deck CurrentSelectedDeck { get; set; }
		public SecretsManager SecretsManager { get; }
		public List<Card> DrawnLastGame { get; set; }
		public Dictionary<int, Entity> Entities { get; } = new Dictionary<int, Entity>();
		public GameMetaData MetaData { get; } = new GameMetaData();
		internal List<Tuple<int, List<string>>> StoredPowerLogs { get; } = new List<Tuple<int, List<string>>>();
		internal Dictionary<int, string> StoredPlayerNames { get; } = new Dictionary<int, string>();
		internal GameStats StoredGameStats { get; set; }
		public List<AccountId> AccountIds { get; } = new List<AccountId>();

		public Mode CurrentMode
		{
			get => _currentMode;
			set
			{
				_currentMode = value;
				Log.Info(value.ToString());
			}
		}

		private FormatType _currentFormat = FormatType.FT_UNKNOWN;
		public Format? CurrentFormat => HearthDbConverter.GetFormat(_currentFormat);

		public Mode PreviousMode { get; set; }

		public bool SavedReplay { get; set; }

		public Entity PlayerEntity => Entities.FirstOrDefault(x => x.Value?.IsPlayer ?? false).Value;

		public Entity OpponentEntity => Entities.FirstOrDefault(x => x.Value != null && x.Value.HasTag(GameTag.PLAYER_ID) && !x.Value.IsPlayer).Value;

		public Entity GameEntity => Entities.FirstOrDefault(x => x.Value?.Name == "GameEntity").Value;

		public bool IsMulliganDone
		{
			get
			{
				var player = Entities.FirstOrDefault(x => x.Value.IsPlayer);
				var opponent = Entities.FirstOrDefault(x => x.Value.HasTag(GameTag.PLAYER_ID) && !x.Value.IsPlayer);
				if(player.Value == null || opponent.Value == null)
					return false;
				return player.Value.GetTag(GameTag.MULLIGAN_STATE) == (int)Mulligan.DONE
					   && opponent.Value.GetTag(GameTag.MULLIGAN_STATE) == (int)Mulligan.DONE;
			}
		}

		public bool Spectator { get; private set; }

		public GameMode CurrentGameMode
		{
			get
			{
				if(Spectator)
					return GameMode.Spectator;
				if(_currentGameMode == GameMode.None)
					_currentGameMode = HearthDbConverter.GetGameMode(CurrentGameType);
				return _currentGameMode;
			}
		}

		public GameType CurrentGameType { get; private set; }

		public MatchInfo MatchInfo { get; private set; }

		internal void CacheBrawlInfo() => _brawlInfo = Reflection.GetBrawlInfo();
		public BrawlInfo BrawlInfo => _brawlInfo ?? (_brawlInfo = Reflection.GetBrawlInfo());

		private bool _gameDataCacheInvalid = true;

		internal void InvalidateGameDataCache() => _gameDataCacheInvalid = true;

		internal async void CacheGameData()
		{
			if(!_gameDataCacheInvalid)
			{
				Log.Info("Reflection cache still valid");
				LogReflectionData(nameof(CacheGameData));
				return;
			}
			Log.Info("Updating reflection data...");
			_gameDataCacheInvalid = false;
			MatchInfo matchInfo;
			while(!AccountIds.Any() || !GetValidMatchInfo(out matchInfo))
				await Task.Delay(1000);
			MatchInfo = matchInfo;
			Player.Name = matchInfo.LocalPlayer.Name;
			Opponent.Name = matchInfo.OpposingPlayer.Name;
			Player.Id = matchInfo.LocalPlayer.Id;
			Opponent.Id = matchInfo.OpposingPlayer.Id;
			Spectator = Reflection.IsSpectating();
			CurrentGameType = (GameType)Reflection.GetGameType();
			_currentFormat = (FormatType)Reflection.GetFormat();
			MetaData.ServerInfo = Reflection.GetServerInfo();
			LogReflectionData(nameof(CacheGameData));
			if(!string.IsNullOrEmpty(MetaData.ServerInfo?.Address))
			{
				var region = Helper.GetRegionByServerIp(MetaData.ServerInfo.Address);
				if(CurrentRegion == Region.UNKNOWN || region == Region.CHINA)
				{
					CurrentRegion = region;
					Log.Info("Set current region to" + region);
				}
			}
		}

		private void LogReflectionData(string source = "") 
			=> Log.Info($@"Player=[{Player.Name}, {Player.Id}], Opponent=[{Opponent.Name}, {Opponent.Id}], Spectator={Spectator}, GameType={CurrentGameType}, Format={_currentFormat}, Game={MetaData.ServerInfo.GameHandle}", source);

		private bool GetValidMatchInfo(out MatchInfo matchInfo)
		{
			var tmpMatchInfo = Reflection.GetMatchInfo();
			var player = tmpMatchInfo?.LocalPlayer;
			var opponent = tmpMatchInfo?.OpposingPlayer;

			if(player != null && opponent != null)
			{
				bool AccountMatch(AccountId acc, AccountId acc2) => acc.Hi == acc2.Hi && acc.Lo == acc2.Lo;
				var playerAcc = AccountIds.FirstOrDefault(a => AccountMatch(a, player.AccountId));
				var opponnetAcc = AccountIds.FirstOrDefault(a => AccountMatch(a, opponent.AccountId));
				if(playerAcc != null && opponnetAcc != null)
				{
					matchInfo = tmpMatchInfo;
					Log.Info("Found valid MatchInfo");
					return true;
				}
			}
			Log.Warn($"Could not find valid MatchInfo - Log=[{string.Join(", ", AccountIds.Select(x => x.Lo))}], Player={player?.AccountId.Lo}, Opponent={opponent?.AccountId.Lo}");
			matchInfo = null;
			return false;
		}

		public void Reset(bool resetStats = true)
		{
			Log.Info("-------- Reset ---------");

			AccountIds.Clear();
			Player.Reset();
			Opponent.Reset();
			if(!_gameDataCacheInvalid && MatchInfo?.LocalPlayer != null && MatchInfo.OpposingPlayer != null)
			{
				Player.Name = MatchInfo.LocalPlayer.Name;
				Opponent.Name = MatchInfo.OpposingPlayer.Name;
				Player.Id = MatchInfo.LocalPlayer.Id;
				Opponent.Id = MatchInfo.OpposingPlayer.Id;
			}
			Entities.Clear();
			SavedReplay = false;
			SecretsManager.Reset();
			Spectator = false;
			_currentGameMode = GameMode.None;
			CurrentGameType = GameType.GT_UNKNOWN;
			_currentFormat = FormatType.FT_UNKNOWN;
			if(!IsInMenu && resetStats)
				CurrentGameStats = new GameStats(GameResult.None, "", "") {PlayerName = "", OpponentName = "", Region = CurrentRegion};
			PowerLog.Clear();

			if(Core.Game != null && Core.Overlay != null)
			{
				Core.UpdatePlayerCards(true);
				Core.UpdateOpponentCards(true);
			}
		}

		public void StoreGameState()
		{
			if(MetaData.ServerInfo.GameHandle == 0)
				return;
			Log.Info($"Storing PowerLog for gameId={MetaData.ServerInfo.GameHandle}");
			StoredPowerLogs.Add(new Tuple<int, List<string>>(MetaData.ServerInfo.GameHandle, new List<string>(PowerLog)));
			if(Player.Id != -1 && !StoredPlayerNames.ContainsKey(Player.Id))
				StoredPlayerNames.Add(Player.Id, Player.Name);
			if(Opponent.Id != -1 && !StoredPlayerNames.ContainsKey(Opponent.Id))
				StoredPlayerNames.Add(Opponent.Id, Opponent.Name);
			if(StoredGameStats == null)
				StoredGameStats = CurrentGameStats;
		}

		public string GetStoredPlayerName(int id) => StoredPlayerNames.TryGetValue(id, out var name) ? name : string.Empty;

		internal void ResetStoredGameState()
		{
			StoredPowerLogs.Clear();
			StoredPlayerNames.Clear();
			StoredGameStats = null;
		}
	}
}
