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
        Task OnAck (CatanMessage message);
        Task AllGames (List<GameInfo> games);
        Task AllPlayers (ICollection<string> players);
        Task AllMessages (List<CatanMessage> messages);

        Task CreateGame (GameInfo gameInfo, string by);

        Task DeleteGame (GameInfo gameInfo, string by);

        Task JoinGame (GameInfo gameInfo, string playerName);

        Task LeaveGame (GameInfo gameInfo, string playerName);

        Task ToAllClients (CatanMessage message);

        Task ToOneClient (CatanMessage message);
        Task ServiceError (CatanMessage message, string error);

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

        public async Task PostMessage (CatanMessage message)
        {
            if (message == null) return;
            if (message.GameInfo == null) return;
            if (message.GameInfo.Id == null) return;

            Game game = Games.GetGame(message.GameInfo.Id);
            if (game != null)
            {
                game.PostLog(message, false);
            }

            string gameId = message.GameInfo.Id.ToString();

            switch (message.MessageType)
            {
                case MessageType.BroadcastMessage:
                    message.MessageType = MessageType.BroadcastMessage;
                    await Clients.Group(gameId).ToAllClients(message);
                    break;
                case MessageType.PrivateMessage:
                    await Clients.Group(gameId).ToAllClients(message);  // no private message for now
                    break;
                case MessageType.CreateGame:
                    
                    //
                    //  do nothing if game is already created
                    if (game == default)
                    {
                        game = new Game() { GameInfo = message.GameInfo};
                        Games.AddGame(message.GameInfo.Id, game);
                        await Groups.AddToGroupAsync(Context.ConnectionId, gameId);
                        game.PostLog(message);
                        //
                        //  tell *all* the clients that a game was created
                        await Clients.All.CreateGame(message.GameInfo, message.GameInfo.Creator);

                    }
                                        
                    break;
                case MessageType.DeleteGame:
                    Games.DeleteGame(message.GameInfo.Id, out Game _);                    
                    //
                    //  tell *all* the clients that a game was deleted
                    await Clients.All.DeleteGame(message.GameInfo, message.From);
                    break;
                case MessageType.JoinGame:

                    //
                    //  this will only fail if the player is already there, but we don't care about that
                    game.NameToPlayerDictionary.TryAdd(message.From, new Player(game.GameLog));
                    
                    await Groups.AddToGroupAsync(Context.ConnectionId, gameId );
                    //
                    //  notify everybody else in the group that somebody has joined the game
                    await Clients.Group(gameId).JoinGame(message.GameInfo, message.From);                    
                    break;
                case MessageType.LeaveGame:
                    game.NameToPlayerDictionary.TryRemove(message.From, out Player _);
                    _ = Groups.RemoveFromGroupAsync(Context.ConnectionId, gameId);
                    await Clients.Group(gameId).LeaveGame(message.GameInfo, message.From);
                    break;
                case MessageType.Ack:
                    await Clients.Group(gameId).OnAck(message);
                    break;
                default:
                    break;
            }

        }

        public async Task SendError (CatanMessage message, string error)
        {
            var errorMessage = new CatanMessage()
            {
                Data = error,
                DataTypeName = typeof(String).FullName,
                From = message.From,
                GameInfo = message.GameInfo,
                ActionType = ActionType.Normal,
                MessageId = message.MessageId
                

            };
            await Clients.Group(message.GameInfo.Id.ToString()).ToAllClients(errorMessage);
        }

      


        public Task Reset ()
        {
            Games = new Games();
            return Task.CompletedTask;
        }

        public Task Register (string playerName)
        {
            PlayerToConnectionDictionary.AddOrUpdate(playerName, Context.ConnectionId, (key, oldValue) => Context.ConnectionId);
            ConnectionToPlayerDictionary.AddOrUpdate(Context.ConnectionId, playerName, (key, oldValue) => playerName);
            return Task.CompletedTask;
        }

        public async Task GetAllGames ()
        {
            var games = Games.GetGames();
            await Clients.Caller.AllGames(games);
        }

        public async Task GetAllMessage (GameInfo gameInfo)
        {
            if (gameInfo == null) return;
            Game game = Games.GetGame(gameInfo.Id);
            if (game != null)
            {
                List<CatanMessage> messages = new List<CatanMessage>();
                messages.AddRange(game.GameLog.ToArray());
                await Clients.Caller.AllMessages(messages);
            }
        }

        public async Task GetPlayersInGame (Guid gameId)
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

              

        public override async Task OnConnectedAsync ()
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AllUsers);
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync (Exception exception)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, AllUsers);
            if (ConnectionToPlayerDictionary.TryGetValue(Context.ConnectionId, out string playerName))
            {
                PlayerToConnectionDictionary.TryRemove(playerName, out string _);
                ConnectionToPlayerDictionary.TryRemove(Context.ConnectionId, out string _);
            }
            await base.OnDisconnectedAsync(exception);
        }
        public Task SendPrivateMessage (string toName, CatanMessage message)
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