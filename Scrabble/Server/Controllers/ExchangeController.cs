﻿
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Scrabble.Core;
using Scrabble.Core.Types;
using Scrabble.Server.Data;
using Scrabble.Shared;
//using System.Text.Json;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Scrabble.Server.Hubs;
using Scrabble.Server.Services;

namespace Scrabble.Server.Controllers
{
    [ApiController]
    [Authorize]
    public class ExchangeController : Controller
    {
        private readonly ScrabbleDbContext scrabbleDb;
        private readonly IHubContext<MoveHub> moveHubContext;
        IMyEmailSender emailSender;
        private readonly ILogger<ExchangeController> _logger;

        public ExchangeController(ScrabbleDbContext scrabbleDb, IHubContext<MoveHub> moveHubContext, IMyEmailSender emailSender, ILogger<ExchangeController> logger)
        {
            this.scrabbleDb = scrabbleDb;
            this.moveHubContext = moveHubContext;
            this.emailSender = emailSender;
            this._logger = logger;
        }


        /// <summary>
        /// Perform exchange and return new game to caller
        /// </summary>
        /// <param name="gameId">ID of requested game</param>
        /// <returns>Requested GameDto</returns>
        [HttpPost("api/[controller]/{gameId}")]

        public async Task<ActionResult<GameDto>> GetGame(int gameId, [FromBody] List<Tile> allTilesInRack)
        {
            var game = await (from g in scrabbleDb.Games
                              where g.GameId == gameId
                              select g).FirstOrDefaultAsync();

            if (game == null)
            {
                return NotFound();
            }

            var gameStateDto = JsonConvert.DeserializeObject<GameStateDto>(game.GameState);
            var playerGame = await PlayerAuthorizedForGame(gameId);
            if (playerGame == null)
            {
                return Forbid();
            }

            var newGame = MakeExchange(gameStateDto, allTilesInRack);
            if (newGame.FinalGameStatus != null)
            {
                // Could be winner if computer used all tiles
                game.WinType = (int)newGame.FinalGameStatus.Win_Type;
                if (newGame.FinalGameStatus.Win_Type == WinTypes.WinType.Win)
                {
                    game.WinnerName = newGame.FinalGameStatus.WinningPlayerName;
                    game.WinnerId = newGame.FinalGameStatus.WinningPlayerId;
                    game.Active = false;
                }
            }
            gameStateDto = new GameStateDto(newGame);
            var gameStateSerialized = JsonConvert.SerializeObject(gameStateDto);

            game.GameState = gameStateSerialized;
            game.LastMoveOn = DateTime.UtcNow;
            scrabbleDb.Update(game);

            // Update player game score summaries
            var players = await (from pg in scrabbleDb.PlayerGames
                                 where pg.GameId == game.GameId
                                 select pg).ToListAsync();
            foreach (var pg in players)
            {
                UpdatePlayerScore(pg, newGame);
            }

            await scrabbleDb.SaveChangesAsync();


            var gameChats = await (from c in scrabbleDb.Chats
                                   where c.GameId == gameId
                                   orderby c.ChatDate ascending
                                   select c).ToListAsync();

            var gameDto = new GameDto();
            gameDto.GameId = gameId;
            gameDto.HighestChatSeen = playerGame.HighestChatSeen;
            gameDto.GameState_Dto = gameStateDto;
            gameDto.Chat_Dto = ChatToDto.GetChatToDto(gameChats);

            await NotifyOfMove(playerGame.PlayerId, gameId, players, gameStateDto);

            return Ok(gameDto);
        }


        /// <summary>
        /// Send out notification that it's their turn
        /// </summary>
        /// <param name="movedPlayerId"></param>
        /// <param name="players"></param>
        private async Task NotifyOfMove(int movedPlayerId, int gameId, List<PlayerGame> players, GameStateDto gameStateDto)
        {
            // Exclude player who made move
            var otherPlayersList = players.Clone();
            foreach (var player in otherPlayersList)
            {
                if (player.PlayerId == movedPlayerId)
                {
                    otherPlayersList.Remove(player);
                    break;
                }
            }

            // 1. Notify all other interactive logins
            var otherPlayerIds = otherPlayersList.Select((pl) => pl.PlayerId).ToList();
            var notifiedIds = await MoveHub.BroadcastMoveTo(moveHubContext.Clients, gameId, otherPlayerIds);

            // 2. Notify next player by email if not logged in
            var currentPlayer = gameStateDto.GamePlayerList[gameStateDto.CurrentPlayerIndex];
            if ((currentPlayer.PlayerId != movedPlayerId) && !notifiedIds.Contains(currentPlayer.PlayerId))
            {
                var nextPlayer = await (from pl in scrabbleDb.Players
                                        where pl.PlayerId == currentPlayer.PlayerId
                                        select pl).FirstOrDefaultAsync();

                if (nextPlayer.NotifyNewMoveByEmail)
                {
                    var scores = "";
                    foreach (var player in gameStateDto.GamePlayerList)
                    {
                        scores += $"{player.Name}: {player.Score}\n";
                    }
                    var baseUri = $"{Request.Scheme}://{Request.Host}";  // :{Request.Host.Port ?? 80}
                    var notifyMessage = $"It's now your turn at Scrabble.  {gameStateDto.LastMoveResult}.  Make your move at {baseUri}/game/{gameId}\n{scores}";
                    var sendTask = emailSender.SendEmailAsync(nextPlayer.Email, "Your move at Scrabble!", notifyMessage);
                }

            }

        }


        private async Task<PlayerGame> PlayerAuthorizedForGame(int gameId)
        {
            var email = User.FindFirst(AppEmailClaimType.ThisAppEmailClaimType).Value;

            if (string.IsNullOrEmpty(email))
            {
                return null;
            }

            var requestingPlayer = await (from pl in scrabbleDb.Players
                                          where pl.Email == email && pl.IsPlayer
                                          select pl).FirstOrDefaultAsync();

            if (requestingPlayer == null) return null;

            var playerGame = await (from pg in scrabbleDb.PlayerGames
                                    where pg.GameId == gameId && pg.PlayerId == requestingPlayer.PlayerId
                                    select pg).FirstOrDefaultAsync();

            return playerGame;
        }


        private void UpdatePlayerScore(PlayerGame pg, GameState game)
        {
            foreach (var player in game.Players)
            {
                if (player.PlayerId == pg.PlayerId)
                {
                    pg.PlayerScore = player.Score;
                    pg.MyMove = player.MyTurn;
                    scrabbleDb.Update(pg);
                }
            }
        }


        /// <summary>
        /// Generate exchange
        /// </summary>
        /// <param name="allTilesInRack">All tiles in rack - swapping tiles have marked swap property</param>
        private GameState MakeExchange(GameStateDto gameStateDto, List<Tile> allTilesInRack)
        {
            var currentGame = new GameState(new List<Scrabble.Core.Types.Player>(), Utility.WordLookupSingleton.Instance);
            UpdateGameStateFromDto.UpdateGameState(currentGame, gameStateDto);
            Setup.SetupComputer(currentGame);  // In case computer is one of the players

            // Place tile(s) in game and move
            currentGame.CurrentPlayer.Tiles.Clear();
            currentGame.CurrentPlayer.Tiles.AddRange(allTilesInRack);

            var swappingTiles = new List<Tile>();
            foreach (var tile in allTilesInRack)
            {
                if (tile.SelectedForSwap)
                {
                    swappingTiles.Add(tile);
                    tile.SelectedForSwap = false;
                }
            }
            var dumpTurn = new DumpLetters(swappingTiles);
            currentGame.CurrentPlayer.TakeTurn(currentGame, dumpTurn);

            return currentGame;

        }
    }

}
