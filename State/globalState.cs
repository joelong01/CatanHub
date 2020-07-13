using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Catan.Proxy;
using Microsoft.AspNetCore.Http;

namespace CatanHub.State
{
    public static class TaskExtensions
    {
        #region Methods

        public static async Task<TResult> TimeoutAfter<TResult>(this Task<TResult> task, TimeSpan timeout)
        {
            using (var timeoutCancellationTokenSource = new CancellationTokenSource())
            {
                var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
                if (completedTask != task) throw new TimeoutException();
                timeoutCancellationTokenSource.Cancel();
                return await task; // Very important in order to propagate exceptions

            }
        }

        #endregion Methods
    }

    public class Game
    {
        #region Delegates + Fields + Events + Enums

        private int GlobalSequnceNumber;

        #endregion Delegates + Fields + Events + Enums

        #region Properties

        public GameInfo GameInfo { get; set; }

        /// <summary>
        ///     All the logs for the entire game
        /// </summary>
        [JsonIgnore]
        public ConcurrentQueue<CatanMessage> GameLog { get; } = new ConcurrentQueue<CatanMessage>();

        /// <summary>
        ///     Given a playerName (CASE SENSItiVE), get the PlayerObject
        /// </summary>

        public ConcurrentDictionary<string, Player> NameToPlayerDictionary { get; } =
            new ConcurrentDictionary<string, Player>();

        public bool Started { get; set; } = false;

        #endregion Properties

        #region Methods

        public int GetNextSequenceNumber()
        {
            return Interlocked.Increment(ref GlobalSequnceNumber);
        }

        public bool PostLog(CatanMessage message, bool addToPlayerLog = true)
        {
            message.Sequence = Interlocked.Increment(ref GlobalSequnceNumber);
            GameLog.Enqueue(message);
            if (addToPlayerLog)
            {
                foreach (var player in NameToPlayerDictionary.Values)
                {
                    player.PlayerLog.Enqueue(message);
                }
            }

            return true;
        }

      

        #endregion Methods
    }

    public class Games
    {
        #region Delegates + Fields + Events + Enums

     

        #endregion Delegates + Fields + Events + Enums

        #region Properties

        private ConcurrentDictionary<Guid, Game> GameDictionary { get; } = new ConcurrentDictionary<Guid, Game>();

        private ConcurrentQueue<(Guid, byte[])> HistoricalMessages { get; set; } =
            new ConcurrentQueue<(Guid, byte[])>();

        
        #endregion Properties

        #region Methods

        public bool AddGame(Guid id, Game game)
        {
            if (GameDictionary.ContainsKey(id))
                return false;

            GameDictionary.TryAdd(id, game);
            return true;
        }

        public bool DeleteGame(Guid id, out Game game)
        {
            return GameDictionary.TryRemove(id, out game);
        }

        public Game GetGame(Guid id)
        {
            var exists = GameDictionary.TryGetValue(id, out var game);
            return exists ? game : null;
        }

        public List<GameInfo> GetGames()
        {
            var games = new List<GameInfo>();
            foreach (var kvp in GameDictionary)
            {
                games.Add(kvp.Value.GameInfo);
            }

            return games;
        }

        public Player GetPlayer(Guid key, string playerName)
        {
            var game = GetGame(key);
            if (game == default) return null;
            game.NameToPlayerDictionary.TryGetValue(playerName, out var player);
            return player;
        }

        
        #endregion Methods
    }

    public class Player
    {
        #region Properties

        [JsonIgnore] public ConcurrentQueue<CatanMessage> PlayerLog { get; } = new ConcurrentQueue<CatanMessage>();
        #endregion Properties

        #region Constructors + Destructors

        //
        // when a player joins a game, we create a Player object.  we start with all the Messages for the whole game so that the player can catch up.
        //
        public Player(ConcurrentQueue<CatanMessage> gameLog)
        {
            foreach (var message in gameLog)
            {
                PlayerLog.Enqueue(message);
            }
        }

        private Player()
        {
        }

        #endregion Constructors + Destructors

        #region Methods

        /// <summary>
        ///     in a thread safe way, return the list of all of the log entries since the last time the API was called.
        /// </summary>
        /// <returns></returns>
        public List<CatanMessage> GetLogEntries()
        {
            var list = new List<CatanMessage>();
            while (PlayerLog.IsEmpty == false)
            {
                if (PlayerLog.TryDequeue(out var message))
                {
                    list.Add(message);
                }
            }

            return list;
        }

        #endregion Methods
    }

   
}