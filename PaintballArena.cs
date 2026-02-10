using System;
using System.Collections.Generic;
using System.Linq;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("PaintballArena", "Copilot", "0.1.0")]
    [Description("Paintball arena queues, matches, and UI for Rust paintball events.")]
    public class PaintballArena : RustPlugin
    {
        private const string PaintballGun = "paintballgun";
        private const string PaintballSuit = "paintballoveralls.suit";
        private const string PaintballAmmo = "ammo.paintball";
        private const string UiScoreboard = "PB_UI_SCOREBOARD";
        private const string UiKillFeed = "PB_UI_KILLFEED";
        private const string UiQueue = "PB_UI_QUEUE";
        private const string UiAdmin = "PB_UI_ADMIN";
        private const string UiMvp = "PB_UI_MVP";

        private StoredData storedData;
        private ConfigData config;

        private readonly Dictionary<ulong, PlayerSession> sessions = new Dictionary<ulong, PlayerSession>();
        private readonly Dictionary<NetworkableId, TriggerDefinition> triggerLookup = new Dictionary<NetworkableId, TriggerDefinition>();
        private readonly Dictionary<ArenaMode, Queue<TeamColor>> queues = new Dictionary<ArenaMode, Queue<TeamColor>>();
        private readonly Dictionary<ArenaMode, Match> activeMatches = new Dictionary<ArenaMode, Match>();
        private Timer queueUiTimer;

        private void Init()
        {
            LoadConfigValues();
            LoadData();
            InitializeQueues();
            cmd.AddChatCommand("pbadmin", this, nameof(CmdPaintballAdmin));
            cmd.AddConsoleCommand("pbadmin.setspawn", this, nameof(ConsoleSetSpawn));
            cmd.AddConsoleCommand("pbadmin.setsphere", this, nameof(ConsoleSetSphere));
            cmd.AddConsoleCommand("pbadmin.resetmatch", this, nameof(ConsoleResetMatch));
        }

        private void OnServerInitialized()
        {
            SpawnStoredTriggers();
            queueUiTimer = timer.Every(2f, UpdateQueueUiForLobbyPlayers);
        }

        private void Unload()
        {
            queueUiTimer?.Destroy();
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyAllUi(player);
            }

            foreach (var entityId in triggerLookup.Keys.ToList())
            {
                var baseEntity = BaseNetworkable.serverEntities.Find(entityId) as BaseEntity;
                baseEntity?.Kill();
            }

            triggerLookup.Clear();
            SaveData();
        }

        private void InitializeQueues()
        {
            queues[ArenaMode.FiveVsFive] = new Queue<TeamColor>();
            queues[ArenaMode.TwoVsTwo] = new Queue<TeamColor>();
            queues[ArenaMode.OneVsOne] = new Queue<TeamColor>();
        }

        private void LoadConfigValues()
        {
            config = Config.ReadObject<ConfigData>();
            if (config?.Teams == null || config.Teams.Count == 0)
            {
                config = ConfigData.CreateDefault();
                SaveConfig();
            }
        }

        protected override void LoadDefaultConfig()
        {
            config = ConfigData.CreateDefault();
        }

        private void SaveConfig()
        {
            Config.WriteObject(config, true);
        }

        private void LoadData()
        {
            storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name) ?? new StoredData();
            storedData.SpawnPoints ??= new Dictionary<string, SerializableVector3>();
            storedData.Spheres ??= new List<SphereData>();
            storedData.PlayerStats ??= new Dictionary<ulong, PlayerStats>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            HandlePlayerLeft(player);
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            if (player == null) return;
            if (!sessions.TryGetValue(player.userID, out var session))
            {
                return;
            }

            if (session.IsSpectator && !session.InMatch)
            {
                TeleportToSpectator(player, session.QueuedMode ?? ArenaMode.None);
                return;
            }

            if (session.InMatch)
            {
                var match = GetMatch(session.Mode);
                if (match == null)
                {
                    return;
                }

                if (session.IsEliminated)
                {
                    TeleportToSpectator(player, match.Mode);
                    return;
                }

                EquipPaintballKit(player, session.Team, session.Mode);
            }
        }

        private void OnWeaponReload(BaseProjectile projectile, BasePlayer player)
        {
            if (projectile == null || player == null) return;
            if (!IsPaintballGun(projectile)) return;
            if (!sessions.TryGetValue(player.userID, out var session)) return;
            ForceTeamAmmo(player, session.Team, 0.1f, session.Mode);
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return null;
            var victim = entity as BasePlayer;
            if (victim == null || info.InitiatorPlayer == null) return null;

            if (!IsPaintballHit(info)) return null;

            info.damageTypes.ScaleAll(0f);
            info.HitMaterial = 0;

            var attacker = info.InitiatorPlayer;
            HandlePaintballHit(attacker, victim);
            return null;
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (attacker == null || info == null) return;
            if (!IsPaintballHit(info)) return;

            if (!sessions.TryGetValue(attacker.userID, out var session) || !session.InMatch) return;
            var match = GetMatch(session.Mode);
            if (match == null || !match.RoundInProgress) return;

            match.RecordShot(attacker.userID);
        }

        private void OnEntityEnter(TriggerBase trigger, BaseEntity entity)
        {
            var player = entity as BasePlayer;
            if (player == null) return;
            if (trigger == null) return;

            var triggerEntity = trigger.GetComponent<BaseEntity>();
            if (triggerEntity == null) return;
            if (!triggerLookup.TryGetValue(triggerEntity.net.ID, out var definition)) return;

            switch (definition.Type)
            {
                case TriggerType.LobbyJoin:
                    EnterLobby(player);
                    break;
                case TriggerType.LobbyLeave:
                    LeaveLobby(player);
                    break;
                case TriggerType.Team:
                    SetPlayerTeam(player, definition.Team);
                    break;
                case TriggerType.Mode:
                    QueueTeamForMode(player, definition.Mode);
                    break;
            }
        }

        private void CmdPaintballAdmin(BasePlayer player, string command, string[] args)
        {
            if (player == null || !player.IsAdmin)
            {
                return;
            }

            OpenAdminUi(player);
        }

        private void ConsoleSetSpawn(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !player.IsAdmin) return;

            var spawnKey = arg.GetString(0, string.Empty);
            if (string.IsNullOrEmpty(spawnKey)) return;

            storedData.SpawnPoints[spawnKey] = SerializableVector3.From(player.transform.position);
            SaveData();
            player.ChatMessage($"Paintball spawn '{spawnKey}' saved.");
        }

        private void ConsoleSetSphere(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !player.IsAdmin) return;

            var type = arg.GetString(0, string.Empty);
            var value = arg.GetString(1, string.Empty);
            if (string.IsNullOrEmpty(type)) return;

            var sphere = new SphereData
            {
                Type = type,
                Value = value,
                Position = SerializableVector3.From(GetLookPosition(player)),
                Radius = config.TriggerRadius
            };

            storedData.Spheres.Add(sphere);
            SaveData();
            SpawnTrigger(sphere);
            player.ChatMessage($"Paintball sphere '{type} {value}' created.");
        }

        private void ConsoleResetMatch(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !player.IsAdmin) return;

            var modeName = arg.GetString(0, string.Empty);
            if (!Enum.TryParse(modeName, true, out ArenaMode mode))
            {
                player.ChatMessage("Specify mode: OneVsOne, TwoVsTwo, FiveVsFive.");
                return;
            }

            EndMatch(mode, true);
            player.ChatMessage($"Match reset for {mode}.");
        }

        private void EnterLobby(BasePlayer player)
        {
            var session = GetSession(player);
            if (!session.InLobby)
            {
                session.InLobby = true;
                session.PreLobbyPosition = SerializableVector3.From(player.transform.position);
            }
            session.IsSpectator = false;

            if (TryGetSpawn("Lobby", out var lobbyPos))
            {
                TeleportPlayer(player, lobbyPos);
            }

            EnsureLobbyUi(player);
        }

        private void LeaveLobby(BasePlayer player)
        {
            var session = GetSession(player);
            session.InLobby = false;
            if (session.Team != TeamColor.None)
            {
                RemoveTeamFromQueues(session.Team);
            }
            session.Team = TeamColor.None;
            session.QueuedMode = null;
            session.IsSpectator = false;

            CleanupInventory(player);
            if (session.PreLobbyPosition.HasValue)
            {
                TeleportPlayer(player, session.PreLobbyPosition.Value);
            }

            DestroyAllUi(player);
        }

        private void SetPlayerTeam(BasePlayer player, TeamColor team)
        {
            var session = GetSession(player);
            session.Team = team;

            ApplyTeamSuit(player, team);
            ForceTeamAmmo(player, team, 0.1f);
            player.ChatMessage($"Joined {team} team.");
        }

        private void QueueTeamForMode(BasePlayer player, ArenaMode mode)
        {
            var session = GetSession(player);
            if (session.Team == TeamColor.None)
            {
                player.ChatMessage("Pick a team before queueing.");
                return;
            }

            if (session.InMatch)
            {
                player.ChatMessage("Already in a match.");
                return;
            }

            if (!session.InLobby)
            {
                player.ChatMessage("Enter the lobby first.");
                return;
            }

            if (session.QueuedMode == mode)
            {
                player.ChatMessage($"Already queued for {mode}.");
                return;
            }

            RemoveTeamFromQueues(session.Team);
            queues[mode].Enqueue(session.Team);
            foreach (var teammate in GetTeamPlayers(session.Team))
            {
                GetSession(teammate).QueuedMode = mode;
            }
            player.ChatMessage($"Queued {session.Team} for {mode}.");
            if (activeMatches.ContainsKey(mode))
            {
                MoveTeamToSpectator(session.Team, mode);
            }
            TryStartMatch(mode);
        }

        private void RemoveTeamFromQueues(TeamColor team)
        {
            foreach (var mode in queues.Keys.ToList())
            {
                queues[mode] = new Queue<TeamColor>(queues[mode].Where(t => t != team));
            }

            foreach (var player in GetTeamPlayers(team))
            {
                var session = GetSession(player);
                session.QueuedMode = null;
                session.IsSpectator = false;
            }
        }

        private void TryStartMatch(ArenaMode mode)
        {
            if (activeMatches.ContainsKey(mode)) return;
            var queue = queues[mode];
            if (queue.Count < 2) return;

            var teamA = queue.Dequeue();
            var teamB = queue.Dequeue();
            var required = GetRequiredTeamSize(mode);
            if (GetTeamPlayers(teamA).Count() < required || GetTeamPlayers(teamB).Count() < required)
            {
                queue.Enqueue(teamA);
                queue.Enqueue(teamB);
                return;
            }

            StartMatch(mode, teamA, teamB);
        }

        private void StartMatch(ArenaMode mode, TeamColor teamA, TeamColor teamB)
        {
            var match = new Match(mode, teamA, teamB);
            activeMatches[mode] = match;

            if (!TryGetArenaSpawns(mode, out var spawnA, out var spawnB))
            {
                PrintWarning($"No arena configured for {mode}.");
                return;
            }

            var teamAPlayers = GetTeamPlayers(teamA);
            var teamBPlayers = GetTeamPlayers(teamB);

            foreach (var player in teamAPlayers)
            {
                var session = GetSession(player);
                session.InMatch = true;
                session.InLobby = false;
                session.Mode = mode;
                session.IsEliminated = false;
                session.QueuedMode = null;
                session.IsSpectator = false;
                TeleportPlayer(player, spawnA);
                EquipPaintballKit(player, teamA, mode);
                match.AddPlayer(player.userID, true);
            }

            foreach (var player in teamBPlayers)
            {
                var session = GetSession(player);
                session.InMatch = true;
                session.InLobby = false;
                session.Mode = mode;
                session.IsEliminated = false;
                session.QueuedMode = null;
                session.IsSpectator = false;
                TeleportPlayer(player, spawnB);
                EquipPaintballKit(player, teamB, mode);
                match.AddPlayer(player.userID, false);
            }

            StartRound(match);
        }

        private void StartRound(Match match)
        {
            match.Round++;
            match.ResetAlive();
            match.RoundInProgress = true;

            if (!TryGetArenaSpawns(match.Mode, out var spawnA, out var spawnB)) return;

            foreach (var playerId in match.TeamAPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                var session = GetSession(player);
                session.IsEliminated = false;
                SpawnPlayer(player, spawnA);
                EquipPaintballKit(player, match.TeamA, match.Mode);
            }

            foreach (var playerId in match.TeamBPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                var session = GetSession(player);
                session.IsEliminated = false;
                SpawnPlayer(player, spawnB);
                EquipPaintballKit(player, match.TeamB, match.Mode);
            }

            ApplyRoundBarrier(match, 5f);
            UpdateScoreboard(match);
        }

        private void EndRound(Match match, TeamColor winner)
        {
            if (winner == match.TeamA) match.ScoreA++;
            else if (winner == match.TeamB) match.ScoreB++;

            match.RoundInProgress = false;
            UpdateScoreboard(match);

            var targetRounds = config.GetRoundsToWin(match.Mode);
            if (match.ScoreA >= targetRounds || match.ScoreB >= targetRounds)
            {
                EndMatch(match.Mode, false);
                return;
            }

            timer.Once(3f, () => StartRound(match));
        }

        private void EndMatch(ArenaMode mode, bool forced)
        {
            if (!activeMatches.TryGetValue(mode, out var match))
            {
                return;
            }

            activeMatches.Remove(mode);
            SaveMatchStats(match);

            foreach (var playerId in match.AllPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;

                var session = GetSession(player);
                session.InMatch = false;
                session.InLobby = true;
                session.Mode = ArenaMode.None;
                session.IsEliminated = false;
                session.IsSpectator = false;

                if (!forced)
                {
                    TeleportToSpectator(player, mode);
                }

                DestroyAllUi(player);
            }

            if (!forced && mode == ArenaMode.FiveVsFive)
            {
                ShowMvpScreen(match);
            }

            TryStartMatch(mode);
        }

        private void HandlePaintballHit(BasePlayer attacker, BasePlayer victim)
        {
            if (attacker == null || victim == null) return;
            if (!sessions.TryGetValue(attacker.userID, out var attackerSession) || !attackerSession.InMatch) return;
            if (!sessions.TryGetValue(victim.userID, out var victimSession) || !victimSession.InMatch) return;
            if (attackerSession.Mode != victimSession.Mode) return;

            if (attackerSession.Team == victimSession.Team)
            {
                return;
            }

            if (victimSession.IsEliminated)
            {
                return;
            }

            var match = GetMatch(attackerSession.Mode);
            if (match == null || !match.RoundInProgress) return;

            match.RecordHit(attacker.userID);
            match.RecordKill(attacker.userID);
            match.EliminatePlayer(victim.userID, victimSession.Team == match.TeamA);

            PlayHitEffect(attacker, victim);

            victimSession.IsEliminated = true;
            victim.Die();

            if (match.Mode != ArenaMode.FiveVsFive)
            {
                AutoReloadOnKill(attacker);
            }

            UpdateKillFeed(attacker, victim, attackerSession.Team);
            CheckRoundEnd(match);
        }

        private void HandlePlayerLeft(BasePlayer player)
        {
            if (!sessions.TryGetValue(player.userID, out var session))
            {
                return;
            }

            if (!session.InMatch)
            {
                sessions.Remove(player.userID);
                if (session.Team != TeamColor.None && !GetTeamPlayers(session.Team).Any())
                {
                    RemoveTeamFromQueues(session.Team);
                }
                return;
            }

            var match = GetMatch(session.Mode);
            if (match == null) return;

            session.IsEliminated = true;
            match.RemovePlayer(player.userID, session.Team == match.TeamA);
            if (match.TeamAPlayers.Count == 0 || match.TeamBPlayers.Count == 0)
            {
                EndMatch(match.Mode, false);
                return;
            }
            CheckRoundEnd(match);
        }

        private void CheckRoundEnd(Match match)
        {
            if (!match.RoundInProgress) return;
            if (match.TeamAAlive.Count == 0)
            {
                EndRound(match, match.TeamB);
                return;
            }

            if (match.TeamBAlive.Count == 0)
            {
                EndRound(match, match.TeamA);
            }
        }

        private void ApplyRoundBarrier(Match match, float seconds)
        {
            foreach (var playerId in match.AllPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Frozen, true);
                player.SendNetworkUpdateImmediate();
            }

            timer.Once(seconds, () =>
            {
                foreach (var playerId in match.AllPlayers)
                {
                    var player = BasePlayer.FindByID(playerId);
                    if (player == null) continue;
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.Frozen, false);
                    player.SendNetworkUpdateImmediate();
                }
            });
        }

        private void MoveTeamToSpectator(TeamColor team, ArenaMode mode)
        {
            foreach (var player in GetTeamPlayers(team))
            {
                var session = GetSession(player);
                session.IsSpectator = true;
                session.InLobby = true;
                TeleportToSpectator(player, mode);
                EnsureLobbyUi(player);
            }
        }

        private void AutoReloadOnKill(BasePlayer attacker)
        {
            if (attacker == null) return;
            var weapon = attacker.GetHeldEntity() as BaseProjectile;
            if (weapon == null) return;

            weapon.primaryMagazine.contents = 1;
            weapon.SendNetworkUpdateImmediate();
            if (sessions.TryGetValue(attacker.userID, out var session))
            {
                ForceTeamAmmo(attacker, session.Team, 0.1f, session.Mode);
            }
            Effect.server.Run("assets/prefabs/tools/pumpshotgun/effects/hit.prefab", attacker.transform.position);
        }

        private void PlayHitEffect(BasePlayer attacker, BasePlayer victim)
        {
            Effect.server.Run("assets/prefabs/tools/pumpshotgun/effects/hit.prefab", victim.transform.position);
        }

        private void UpdateScoreboard(Match match)
        {
            var teamAConfig = config.GetTeam(match.TeamA);
            var teamBConfig = config.GetTeam(match.TeamB);

            foreach (var playerId in match.AllPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                CuiHelper.DestroyUi(player, UiScoreboard);

                var container = new CuiElementContainer();
                var panel = container.Add(new CuiPanel
                {
                    Image = { Color = "0.1 0.1 0.1 0.6" },
                    RectTransform = { AnchorMin = "0.33 0.92", AnchorMax = "0.67 0.99" },
                    CursorEnabled = false
                }, "Overlay", UiScoreboard);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{teamAConfig.Name} {match.ScoreA} - {match.ScoreB} {teamBConfig.Name}", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0.3", AnchorMax = "1 1" }
                }, panel);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"Round {match.Round}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.8" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0.4" }
                }, panel);

                CuiHelper.AddUi(player, container);
            }
        }

        private void UpdateKillFeed(BasePlayer attacker, BasePlayer victim, TeamColor team)
        {
            var teamConfig = config.GetTeam(team);
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!sessions.TryGetValue(player.userID, out var session) || !session.InMatch) continue;
                if (session.Mode != GetSession(attacker).Mode) continue;

                CuiHelper.DestroyUi(player, UiKillFeed);
                var container = new CuiElementContainer();
                var panel = container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.6" },
                    RectTransform = { AnchorMin = "0.75 0.82", AnchorMax = "0.98 0.9" },
                    CursorEnabled = false
                }, "Overlay", UiKillFeed);

                container.Add(new CuiLabel
                {
                    Text = { Text = $"{attacker.displayName} ■ {victim.displayName}", FontSize = 12, Align = TextAnchor.MiddleCenter, Color = teamConfig.Color },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, panel);

                CuiHelper.AddUi(player, container);
                timer.Once(3f, () => CuiHelper.DestroyUi(player, UiKillFeed));
            }
        }

        private void UpdateQueueUiForLobbyPlayers()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!sessions.TryGetValue(player.userID, out var session) || !session.InLobby) continue;
                UpdateQueueUi(player);
            }
        }

        private void UpdateQueueUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiQueue);

            var container = new CuiElementContainer();
            var panel = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.45" },
                RectTransform = { AnchorMin = "0.02 0.75", AnchorMax = "0.25 0.9" },
                CursorEnabled = false
            }, "Overlay", UiQueue);

            Match activeMatch = null;
            if (sessions.TryGetValue(player.userID, out var session) && session.QueuedMode.HasValue)
            {
                activeMatches.TryGetValue(session.QueuedMode.Value, out activeMatch);
            }
            if (activeMatch == null)
            {
                activeMatch = activeMatches.Values.FirstOrDefault();
            }
            string progressText = "No active match";
            string nextUp = SessionQueueStatus(player);
            float progressRatio = 0f;

            if (activeMatch != null)
            {
                var targetRounds = config.GetRoundsToWin(activeMatch.Mode);
                var leadScore = Math.Max(activeMatch.ScoreA, activeMatch.ScoreB);
                progressText = $"Match {leadScore}/{targetRounds}";
                progressRatio = targetRounds > 0 ? Mathf.Clamp01(leadScore / (float)targetRounds) : 0f;
            }

            container.Add(new CuiLabel
            {
                Text = { Text = progressText, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0.05 0.55", AnchorMax = "0.95 0.9" }
            }, panel);

            if (progressRatio > 0f)
            {
                container.Add(new CuiPanel
                {
                    Image = { Color = "0.2 0.6 1 0.8" },
                    RectTransform = { AnchorMin = "0.1 0.45", AnchorMax = $"{0.1f + 0.8f * progressRatio} 0.5" }
                }, panel);
            }

            container.Add(new CuiLabel
            {
                Text = { Text = nextUp, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.8" },
                RectTransform = { AnchorMin = "0.05 0.05", AnchorMax = "0.95 0.5" }
            }, panel);

            CuiHelper.AddUi(player, container);
        }

        private string SessionQueueStatus(BasePlayer player)
        {
            if (!sessions.TryGetValue(player.userID, out var session) || session.QueuedMode == null)
            {
                return "Not queued";
            }

            if (session.IsSpectator)
            {
                return "Spectating match";
            }

            var queue = queues[session.QueuedMode.Value];
            var index = queue.ToList().FindIndex(team => team == session.Team);
            return index < 0 ? "Queued" : $"Next up #{index + 1}";
        }

        private void OpenAdminUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiAdmin);
            var container = new CuiElementContainer();
            var panel = container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.75" },
                RectTransform = { AnchorMin = "0.2 0.2", AnchorMax = "0.8 0.8" },
                CursorEnabled = true
            }, "Overlay", UiAdmin);

            AddAdminButton(container, panel, "Set Lobby", "pbadmin.setspawn Lobby", 0.7f);
            AddAdminButton(container, panel, "Set Spectator", "pbadmin.setspawn Spectator", 0.6f);
            AddAdminButton(container, panel, "Arena1 SpawnA", "pbadmin.setspawn Arena1SpawnA", 0.5f);
            AddAdminButton(container, panel, "Arena1 SpawnB", "pbadmin.setspawn Arena1SpawnB", 0.4f);
            AddAdminButton(container, panel, "Arena2 SpawnA", "pbadmin.setspawn Arena2SpawnA", 0.3f);
            AddAdminButton(container, panel, "Arena2 SpawnB", "pbadmin.setspawn Arena2SpawnB", 0.2f);
            AddAdminButton(container, panel, "Arena3 SpawnA", "pbadmin.setspawn Arena3SpawnA", 0.1f);
            AddAdminButton(container, panel, "Arena3 SpawnB", "pbadmin.setspawn Arena3SpawnB", 0.0f);

            var buttonOffsetY = 0.7f;
            foreach (var team in Enum.GetValues(typeof(TeamColor)).Cast<TeamColor>().Where(t => t != TeamColor.None))
            {
                AddAdminButton(container, panel, $"Sphere Team {team}", $"pbadmin.setsphere Team {team}", buttonOffsetY);
                buttonOffsetY -= 0.1f;
            }

            AddAdminButton(container, panel, "Sphere Lobby Join", "pbadmin.setsphere LobbyJoin", buttonOffsetY);
            buttonOffsetY -= 0.1f;
            AddAdminButton(container, panel, "Sphere Lobby Leave", "pbadmin.setsphere LobbyLeave", buttonOffsetY);
            buttonOffsetY -= 0.1f;
            AddAdminButton(container, panel, "Sphere Mode 1v1", "pbadmin.setsphere Mode OneVsOne", buttonOffsetY);
            buttonOffsetY -= 0.1f;
            AddAdminButton(container, panel, "Sphere Mode 2v2", "pbadmin.setsphere Mode TwoVsTwo", buttonOffsetY);
            buttonOffsetY -= 0.1f;
            AddAdminButton(container, panel, "Sphere Mode 5v5", "pbadmin.setsphere Mode FiveVsFive", buttonOffsetY);
            buttonOffsetY -= 0.1f;
            AddAdminButton(container, panel, "Reset Match 5v5", "pbadmin.resetmatch FiveVsFive", buttonOffsetY);

            CuiHelper.AddUi(player, container);
        }

        private void AddAdminButton(CuiElementContainer container, string parent, string text, string command, float offsetY)
        {
            container.Add(new CuiButton
            {
                Button = { Color = "0.2 0.2 0.2 0.8", Command = command },
                RectTransform = { AnchorMin = $"0.05 {offsetY}", AnchorMax = $"0.45 {offsetY + 0.08}" },
                Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" }
            }, parent);
        }

        private void EnsureLobbyUi(BasePlayer player)
        {
            UpdateQueueUi(player);
        }

        private void ShowMvpScreen(Match match)
        {
            var mvp = match.GetMvp();
            if (mvp == null) return;

            foreach (var playerId in match.AllPlayers)
            {
                var player = BasePlayer.FindByID(playerId);
                if (player == null) continue;
                player.SetPlayerFlag(BasePlayer.PlayerFlags.Frozen, true);
                player.SendNetworkUpdateImmediate();
                CuiHelper.DestroyUi(player, UiMvp);

                var container = new CuiElementContainer();
                var panel = container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.8" },
                    RectTransform = { AnchorMin = "0.2 0.35", AnchorMax = "0.8 0.65" },
                    CursorEnabled = false
                }, "Overlay", UiMvp);

                var accuracy = mvp.Value.Shots > 0 ? (mvp.Value.Hits / (float)mvp.Value.Shots) * 100f : 0f;
                container.Add(new CuiLabel
                {
                    Text = { Text = $"MVP: {mvp.Value.Name} ({mvp.Value.Kills} splatters)\nAccuracy: {accuracy:0}%", FontSize = 16, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, panel);

                CuiHelper.AddUi(player, container);
                timer.Once(6f, () =>
                {
                    CuiHelper.DestroyUi(player, UiMvp);
                    if (player != null)
                    {
                        player.SetPlayerFlag(BasePlayer.PlayerFlags.Frozen, false);
                        player.SendNetworkUpdateImmediate();
                    }
                });
            }
        }

        private void DestroyAllUi(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, UiScoreboard);
            CuiHelper.DestroyUi(player, UiKillFeed);
            CuiHelper.DestroyUi(player, UiQueue);
            CuiHelper.DestroyUi(player, UiAdmin);
            CuiHelper.DestroyUi(player, UiMvp);
        }

        private void EquipPaintballKit(BasePlayer player, TeamColor team, ArenaMode mode = ArenaMode.None)
        {
            CleanupInventory(player);
            var suit = ItemManager.CreateByName(PaintballSuit, 1);
            var gun = ItemManager.CreateByName(PaintballGun, 1);
            var ammo = ItemManager.CreateByName(PaintballAmmo, 30);

            if (suit != null)
            {
                suit.skin = config.GetTeam(team).SuitSkin;
                player.inventory.containerWear.GiveItem(suit);
            }

            if (gun != null)
            {
                player.inventory.containerBelt.GiveItem(gun);
            }

            if (ammo != null)
            {
                ammo.skin = config.GetTeam(team).AmmoSkin;
                player.inventory.containerMain.GiveItem(ammo);
            }

            ForceTeamAmmo(player, team, 0.1f, mode);
        }

        private void ApplyTeamSuit(BasePlayer player, TeamColor team)
        {
            var suit = player.inventory.containerWear?.FindItemByItemName(PaintballSuit);
            if (suit != null)
            {
                suit.skin = config.GetTeam(team).SuitSkin;
                suit.MarkDirty();
            }
        }

        private void ForceTeamAmmo(BasePlayer player, TeamColor team, float delay, ArenaMode mode = ArenaMode.None)
        {
            timer.Once(delay, () =>
            {
                if (player == null || !player.IsConnected) return;
                var weapon = player.GetHeldEntity() as BaseProjectile;
                if (weapon == null) return;
                if (!IsPaintballGun(weapon)) return;

                var ammoDef = ItemManager.FindItemDefinition(PaintballAmmo);
                if (ammoDef == null) return;

                var restrictedAmmo = mode == ArenaMode.OneVsOne || mode == ArenaMode.TwoVsTwo;
                weapon.primaryMagazine.contents = restrictedAmmo ? 1 : Math.Max(weapon.primaryMagazine.capacity, 1);
                weapon.primaryMagazine.ammoType = ammoDef;
                weapon.primaryMagazine.contentType = ammoDef.itemid;
                weapon.primaryMagazine.capacity = Math.Max(1, weapon.primaryMagazine.capacity);
                weapon.SendNetworkUpdateImmediate();

                foreach (var item in player.inventory.AllItems())
                {
                    if (item.info.shortname != PaintballAmmo) continue;
                    item.skin = config.GetTeam(team).AmmoSkin;
                    item.MarkDirty();
                }
            });
        }

        private int GetRequiredTeamSize(ArenaMode mode)
        {
            switch (mode)
            {
                case ArenaMode.FiveVsFive:
                    return 5;
                case ArenaMode.TwoVsTwo:
                    return 2;
                case ArenaMode.OneVsOne:
                    return 1;
                default:
                    return 1;
            }
        }

        private bool TryGetArenaSpawns(ArenaMode mode, out SerializableVector3 spawnA, out SerializableVector3 spawnB)
        {
            string keyPrefix;
            switch (mode)
            {
                case ArenaMode.FiveVsFive:
                    keyPrefix = "Arena1";
                    break;
                case ArenaMode.TwoVsTwo:
                    keyPrefix = "Arena2";
                    break;
                case ArenaMode.OneVsOne:
                    keyPrefix = "Arena3";
                    break;
                default:
                    keyPrefix = "Arena3";
                    break;
            }
            if (storedData.SpawnPoints.TryGetValue($"{keyPrefix}SpawnA", out spawnA)
                && storedData.SpawnPoints.TryGetValue($"{keyPrefix}SpawnB", out spawnB))
            {
                return true;
            }

            var arena = config.GetArena(mode);
            if (arena != null)
            {
                spawnA = arena.SpawnA;
                spawnB = arena.SpawnB;
                return true;
            }

            spawnA = SerializableVector3.Zero;
            spawnB = SerializableVector3.Zero;
            return false;
        }

        private void SaveMatchStats(Match match)
        {
            foreach (var entry in match.Stats)
            {
                if (!storedData.PlayerStats.TryGetValue(entry.Key, out var stats))
                {
                    stats = new PlayerStats();
                    storedData.PlayerStats[entry.Key] = stats;
                }

                stats.Name = entry.Value.Name;
                stats.Shots += entry.Value.Shots;
                stats.Hits += entry.Value.Hits;
                stats.Kills += entry.Value.Kills;
            }

            SaveData();
        }

        private void CleanupInventory(BasePlayer player)
        {
            player.inventory.Strip();
        }

        private void TeleportPlayer(BasePlayer player, SerializableVector3 position)
        {
            player.Teleport(position.ToVector3());
        }

        private void SpawnPlayer(BasePlayer player, SerializableVector3 position)
        {
            var vector = position.ToVector3();
            if (!player.IsAlive())
            {
                player.RespawnAt(vector, Quaternion.identity);
            }
            else
            {
                player.Teleport(vector);
                player.health = player.MaxHealth();
                player.metabolism.Reset();
            }
        }

        private void TeleportToSpectator(BasePlayer player, ArenaMode mode)
        {
            if (TryGetSpawn("Spectator", out var spectator))
            {
                TeleportPlayer(player, spectator);
            }
            else
            {
                player.Teleport(player.transform.position + Vector3.up * 2f);
            }

            CleanupInventory(player);
        }

        private Vector3 GetLookPosition(BasePlayer player)
        {
            var ray = player.eyes.HeadRay();
            if (Physics.Raycast(ray, out var hit, 30f, Layers.Mask.Solid))
            {
                return hit.point;
            }

            return player.transform.position;
        }

        private bool TryGetSpawn(string key, out SerializableVector3 spawn)
        {
            return storedData.SpawnPoints.TryGetValue(key, out spawn);
        }

        private IEnumerable<BasePlayer> GetTeamPlayers(TeamColor team)
        {
            return BasePlayer.activePlayerList.Where(player => sessions.TryGetValue(player.userID, out var session) && session.Team == team);
        }

        private PlayerSession GetSession(BasePlayer player)
        {
            if (!sessions.TryGetValue(player.userID, out var session))
            {
                session = new PlayerSession();
                sessions[player.userID] = session;
            }

            return session;
        }

        private Match GetMatch(ArenaMode mode)
        {
            activeMatches.TryGetValue(mode, out var match);
            return match;
        }

        private bool IsPaintballGun(BaseProjectile projectile)
        {
            return projectile?.GetItem()?.info.shortname == PaintballGun;
        }

        private bool IsPaintballHit(HitInfo info)
        {
            if (info == null) return false;
            var weapon = info.Weapon?.GetItem();
            if (weapon == null) return false;
            if (weapon.info.shortname != PaintballGun) return false;
            return info.AmmoType?.shortname == PaintballAmmo;
        }

        private void SpawnStoredTriggers()
        {
            foreach (var sphere in storedData.Spheres)
            {
                SpawnTrigger(sphere);
            }
        }

        private void SpawnTrigger(SphereData data)
        {
            var entity = GameManager.server.CreateEntity("assets/prefabs/trigger/trigger_sphere.prefab", data.Position.ToVector3());
            if (entity == null)
            {
                PrintWarning("Failed to create trigger sphere.");
                return;
            }

            var trigger = entity.GetComponent<TriggerSphere>();
            if (trigger == null)
            {
                entity.Kill();
                return;
            }

            trigger.radius = data.Radius;
            entity.enableSaving = false;
            entity.Spawn();

            var definition = new TriggerDefinition
            {
                Type = ParseTriggerType(data.Type),
                Team = ParseTeam(data.Type, data.Value),
                Mode = ParseMode(data.Type, data.Value)
            };

            triggerLookup[entity.net.ID] = definition;
        }

        private TriggerType ParseTriggerType(string type)
        {
            if (Enum.TryParse(type, true, out TriggerType parsed))
            {
                return parsed;
            }

            return TriggerType.Unknown;
        }

        private TeamColor ParseTeam(string type, string value)
        {
            if (!string.Equals(type, "Team", StringComparison.OrdinalIgnoreCase)) return TeamColor.None;
            if (Enum.TryParse(value, true, out TeamColor team)) return team;
            return TeamColor.None;
        }

        private ArenaMode ParseMode(string type, string value)
        {
            if (!string.Equals(type, "Mode", StringComparison.OrdinalIgnoreCase)) return ArenaMode.None;
            if (Enum.TryParse(value, true, out ArenaMode mode)) return mode;
            return ArenaMode.None;
        }

        private class PlayerSession
        {
            public bool InLobby;
            public TeamColor Team = TeamColor.None;
            public ArenaMode Mode = ArenaMode.None;
            public bool InMatch;
            public bool IsEliminated;
            public bool IsSpectator;
            public ArenaMode? QueuedMode;
            public SerializableVector3? PreLobbyPosition;
        }

        private class Match
        {
            public ArenaMode Mode;
            public TeamColor TeamA;
            public TeamColor TeamB;
            public int ScoreA;
            public int ScoreB;
            public int Round;
            public bool RoundInProgress;
            public HashSet<ulong> TeamAPlayers = new HashSet<ulong>();
            public HashSet<ulong> TeamBPlayers = new HashSet<ulong>();
            public HashSet<ulong> TeamAAlive = new HashSet<ulong>();
            public HashSet<ulong> TeamBAlive = new HashSet<ulong>();
            public Dictionary<ulong, PlayerMatchStats> Stats = new Dictionary<ulong, PlayerMatchStats>();

            public Match(ArenaMode mode, TeamColor teamA, TeamColor teamB)
            {
                Mode = mode;
                TeamA = teamA;
                TeamB = teamB;
            }

            public IEnumerable<ulong> AllPlayers => TeamAPlayers.Concat(TeamBPlayers);

            public void AddPlayer(ulong playerId, bool teamA)
            {
                if (teamA) TeamAPlayers.Add(playerId);
                else TeamBPlayers.Add(playerId);
                Stats[playerId] = new PlayerMatchStats { Name = BasePlayer.FindByID(playerId)?.displayName ?? playerId.ToString() };
            }

            public void RemovePlayer(ulong playerId, bool teamA)
            {
                if (teamA)
                {
                    TeamAPlayers.Remove(playerId);
                    TeamAAlive.Remove(playerId);
                }
                else
                {
                    TeamBPlayers.Remove(playerId);
                    TeamBAlive.Remove(playerId);
                }
            }

            public void EliminatePlayer(ulong playerId, bool teamA)
            {
                if (teamA)
                {
                    TeamAAlive.Remove(playerId);
                }
                else
                {
                    TeamBAlive.Remove(playerId);
                }
            }

            public void ResetAlive()
            {
                TeamAAlive = new HashSet<ulong>(TeamAPlayers);
                TeamBAlive = new HashSet<ulong>(TeamBPlayers);
            }

            public void RecordShot(ulong playerId)
            {
                if (Stats.TryGetValue(playerId, out var stats))
                {
                    stats.Shots++;
                }
            }

            public void RecordHit(ulong playerId)
            {
                if (Stats.TryGetValue(playerId, out var stats))
                {
                    stats.Hits++;
                }
            }

            public void RecordKill(ulong playerId)
            {
                if (Stats.TryGetValue(playerId, out var stats))
                {
                    stats.Kills++;
                }
            }

            public KeyValuePair<ulong, PlayerMatchStats>? GetMvp()
            {
                if (Stats.Count == 0) return null;
                return Stats.OrderByDescending(kvp => kvp.Value.Kills).First();
            }
        }

        private class PlayerMatchStats
        {
            public string Name;
            public int Shots;
            public int Hits;
            public int Kills;
        }

        private class TriggerDefinition
        {
            public TriggerType Type;
            public TeamColor Team;
            public ArenaMode Mode;
        }

        private enum TriggerType
        {
            Unknown,
            LobbyJoin,
            LobbyLeave,
            Team,
            Mode
        }

        private enum TeamColor
        {
            None,
            Green,
            Orange,
            Yellow,
            Blue,
            Purple
        }

        private enum ArenaMode
        {
            None,
            OneVsOne,
            TwoVsTwo,
            FiveVsFive
        }

        private class ConfigData
        {
            public Dictionary<string, TeamConfig> Teams = new Dictionary<string, TeamConfig>();
            public Dictionary<string, ArenaConfig> Arenas = new Dictionary<string, ArenaConfig>();
            public float TriggerRadius = 2.5f;
            public int RoundsToWin5v5 = 10;
            public int RoundsToWin2v2 = 5;
            public int RoundsToWin1v1 = 5;

            public static ConfigData CreateDefault()
            {
                return new ConfigData
                {
                    Teams = new Dictionary<string, TeamConfig>
                    {
                        ["Green"] = new TeamConfig { Name = "Green", Color = "0 1 0 1", SuitSkin = 0, AmmoSkin = 0 },
                        ["Orange"] = new TeamConfig { Name = "Orange", Color = "1 0.5 0 1", SuitSkin = 0, AmmoSkin = 0 },
                        ["Yellow"] = new TeamConfig { Name = "Yellow", Color = "1 0.9 0 1", SuitSkin = 0, AmmoSkin = 0 },
                        ["Blue"] = new TeamConfig { Name = "Blue", Color = "0.3 0.6 1 1", SuitSkin = 0, AmmoSkin = 0 },
                        ["Purple"] = new TeamConfig { Name = "Purple", Color = "0.7 0.3 1 1", SuitSkin = 0, AmmoSkin = 0 }
                    },
                    Arenas = new Dictionary<string, ArenaConfig>
                    {
                        ["FiveVsFive"] = new ArenaConfig { SpawnA = SerializableVector3.Zero, SpawnB = SerializableVector3.Zero },
                        ["TwoVsTwo"] = new ArenaConfig { SpawnA = SerializableVector3.Zero, SpawnB = SerializableVector3.Zero },
                        ["OneVsOne"] = new ArenaConfig { SpawnA = SerializableVector3.Zero, SpawnB = SerializableVector3.Zero }
                    }
                };
            }

            public TeamConfig GetTeam(TeamColor team)
            {
                Teams.TryGetValue(team.ToString(), out var configTeam);
                return configTeam ?? new TeamConfig { Name = team.ToString(), Color = "1 1 1 1" };
            }

            public ArenaConfig GetArena(ArenaMode mode)
            {
                Arenas.TryGetValue(mode.ToString(), out var arena);
                return arena;
            }

            public int GetRoundsToWin(ArenaMode mode)
            {
                switch (mode)
                {
                    case ArenaMode.FiveVsFive:
                        return RoundsToWin5v5;
                    case ArenaMode.TwoVsTwo:
                        return RoundsToWin2v2;
                    case ArenaMode.OneVsOne:
                        return RoundsToWin1v1;
                    default:
                        return 1;
                }
            }
        }

        private class TeamConfig
        {
            public string Name;
            public string Color;
            public ulong SuitSkin;
            public ulong AmmoSkin;
        }

        private class ArenaConfig
        {
            public SerializableVector3 SpawnA;
            public SerializableVector3 SpawnB;
        }

        private class StoredData
        {
            public Dictionary<string, SerializableVector3> SpawnPoints = new Dictionary<string, SerializableVector3>();
            public List<SphereData> Spheres = new List<SphereData>();
            public Dictionary<ulong, PlayerStats> PlayerStats = new Dictionary<ulong, PlayerStats>();
        }

        private class PlayerStats
        {
            public string Name;
            public int Shots;
            public int Hits;
            public int Kills;
        }

        private class SphereData
        {
            public string Type;
            public string Value;
            public SerializableVector3 Position;
            public float Radius;
        }

        private struct SerializableVector3
        {
            public float X;
            public float Y;
            public float Z;

            public static SerializableVector3 Zero => new SerializableVector3 { X = 0, Y = 0, Z = 0 };

            public Vector3 ToVector3() => new Vector3(X, Y, Z);

            public static SerializableVector3 From(Vector3 vector)
            {
                return new SerializableVector3 { X = vector.x, Y = vector.y, Z = vector.z };
            }
        }
    }
}
