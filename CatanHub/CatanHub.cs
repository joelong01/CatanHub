using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;

using Catan.Proxy;

using CatanHub.State;

using Microsoft.AspNetCore.SignalR;

namespace CatanHub

{
    /// <summary>
    ///     these are the signatures of the "OnRecieved" calls on the client
    /// </summary>
    public interface ICatanClient
    {
        #region Methods
        Task OnAck(string fromPlayer, Guid messageId);
        Task AllGames(List<GameInfo> games);
        Task AllPlayers(ICollection<string> players);
        Task AllMessagages(List<CatanMessage> messages);

        Task CreateGame(GameInfo gameInfo, string by);

        Task DeleteGame(Guid id, string by);

        Task JoinGame(GameInfo gameInfo, string playerName);

        Task LeaveGame(GameInfo gameInfo, string playerName);

        Task ToAllClients(CatanMessage message);

        Task ToOneClient(CatanMessage message);

        #endregion Methods
    }

    public class CatanHub : Hub<ICatanClient>
    {
        public static Games Games { get; set; } = new Games();

        #region Properties

        private static ConcurrentDictionary<string, string> PlayerToConnectionDictionary = new ConcurrentDictionary<string, string>();
        private static ConcurrentDictionary<string, string> ConnectionToPlayerDictionary = new ConcurrentDictionary<string, string>();

        private static string AllUsers { get; } = "{158B5187-959E-4A81-A8F9-CD9BE0D30300}";

        #endregion Properties

        #region Methods

        public Task Ack(Guid gameId, string fromPlayer, string toPlayer, Guid messageId)
        {
            Game game = Games.GetGame(gameId);
            if (game != null)
            {
                return Clients.Group(gameId.ToString()).OnAck(fromPlayer, messageId);
            }

            return Task.CompletedTask;
        }

        public Task BroadcastMessage(Guid gameId, CatanMessage message)
        {
            //
            //  need to unmarshal to store the message and set the sequence number
            //  

            Game game = Games.GetGame(gameId);
            if (game != null)
            {
                message.MessageType = MessageType.BroadcastMessage;
                // record in game log, but not player log and set the sequence number
                // note: this means there is no interop between a REST client and a SignalR client.
                game.PostLog(message, false);


                return Clients.Group(gameId.ToString()).ToAllClients(message);
            }

            return Task.CompletedTask;
        }

        public Task Reset()
        {
            Games = new Games();
            return Task.CompletedTask;
        }

        public Task Register(string playerName)
        {
            PlayerToConnectionDictionary.AddOrUpdate(playerName, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);
            ConnectionToPlayerDictionary.AddOrUpdate(Context.ConnectionId, playerName, (key, oldValue) => playerName);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Create the datastructures needed to communicate across machines
        ///     Typically the first API to call in the service
        ///
        /// </summary>
        /// <param name="gameId">a stringified Guid</param>
        /// <param name="clientPlayerId">the name of the player - must be unique inside of game</param>
        /// <param name="jsonGameInfo">he JSON string representing a GameInfo objec</param>
        /// <returns></returns>
        public async Task CreateGame(GameInfo gameInfo)
        {
            Game game = Games.GetGame(gameInfo.Id);
            if (game == default)
            {
                Games.AddGame(gameInfo.Id, new Game() { GameInfo = gameInfo });
                await Groups.AddToGroupAsync(Context.ConnectionId, gameInfo.Id.ToString());
                //
                //  tell *all* the clients that a game was created
                await Clients.All.CreateGame(gameInfo, gameInfo.Creator);
                return;
            }
            //
            //  send the client an error?
        }

        public async Task DeleteGame(Guid id, string by)
        {
            try
            {
                bool success = Games.DeleteGame(id, out Game game);
                if (!success)
                {
                    //
                    // send error
                    return;
                }
                //
                //  tell *all* the clients that a game was deleted
                await Clients.All.DeleteGame(id, by);
            }
            catch
            {
                // swallow
            }
        }

        public async Task GetAllGames()
        {
            var games = Games.GetGames();
            await Clients.Caller.AllGames(games);
        }

        public async Task GetAllMessage(GameInfo gameInfo)
        {
            Game game = Games.GetGame(gameInfo.Id);
            if (game != null)
            {
                List<CatanMessage> messages = new List<CatanMessage>();
                messages.AddRange(game.GameLog.ToArray());
                await Clients.Caller.AllMessagages(messages);
            }
        }

        public async Task GetPlayersInGame(Guid gameId)
        {
            Game game = Games.GetGame(gameId);
            if (game != null)
            {
                await Clients.Caller.AllPlayers(game.NameToPlayerDictionary.Keys);
            }
            else
            {
                await Clients.Caller.AllPlayers(new List<string>());
            }
        }

        public async Task JoinGame(GameInfo gameInfo, string playerName)
        {
            try
            {
                Game game = Games.GetGame(gameInfo.Id);

                if (game == null)
                {
                    //
                    //   send error?
                    return;
                }

                bool success = game.NameToPlayerDictionary.TryAdd(playerName, new Player(game.GameLog));
                string gameId = gameInfo.Id.ToString();
                //
                //  add the client to the game SignalR Group
                await Groups.AddToGroupAsync(Context.ConnectionId, gameId);

                //
                //  notify everybody else in the group that somebody has joined the game
                await Clients.Group(gameId).JoinGame(gameInfo, playerName);

                if (!success)
                {
                    //
                    //    error?
                    return;
                }
            }
            catch
            {
                //
                //  client error
                // Console.Out.WriteLine(e.ToString());
            }
        }

        public async Task LeaveGame(GameInfo gameInfo, string playerName)
        {
            try
            {
                Game game = Games.GetGame(gameInfo.Id);

                if (game == null)
                {
                    // send error
                    return;
                }

                if (game.Started)
                {
                    // different error
                    // Description = $"Player '{playerName}' can't be removed from '{gameInfo.Name}' because it has already been started.",

                    return;
                }

                if (game.GameInfo.Creator == playerName)
                {
                    //
                    //    Description = $"The Creator can't leave their own game.",

                    return;
                }

                //
                //  should already be in here since you shoudl have called Monitor()
                bool success = game.NameToPlayerDictionary.TryRemove(playerName, out Player player);

                if (!success)
                {
                    // Description = $"Player '{playerName}' can't be removed from '{gameInfo.Name}'.",
                    return;
                }

                _ = Groups.RemoveFromGroupAsync(Context.ConnectionId, gameInfo.Id.ToString());

                await Clients.Group(gameInfo.Id.ToString()).LeaveGame(gameInfo, playerName);
            }
            catch
            {
                // Description = $"{this.Request.Path} threw an exception. {e}",
                // Console.Out.WriteLine(e.ToString());
            }
        }

        public override async Task OnConnectedAsync()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AllUsers);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, AllUsers);
            if (ConnectionToPlayerDictionary.TryGetValue(Context.ConnectionId, out string playerName))
            {
                PlayerToConnectionDictionary.TryRemove(playerName, out string _);
                ConnectionToPlayerDictionary.TryRemove(Context.ConnectionId, out string _);
            }
            await base.OnDisconnectedAsync(exception);
        }
        public Task SendPrivateMessage(string toName, CatanMessage message)
        {
            message.ActionType = ActionType.Redo;
            var toId = PlayerToConnectionDictionary[toName];
            // Console.WriteLine($"[ToId: {toId}] for [toName={toName}]");
            //return Clients.User(toId).ToOneClient(message);
            return Clients.All.ToOneClient(message);

        }

        #endregion Methods
    }
}