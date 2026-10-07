// =============================================================================
//  Messages.cs  ·  UnoX.Net
//  Tipos de mensaje del protocolo. Deben coincidir 1:1 con server/src/protocol.js
//  y con los objetos que serializa server/src/session.js.
//
//  Los nombres de campo están en minúscula a propósito: es el formato que emite
//  el servidor en JSON. Renombrarlos aquí rompería el deserializado.
// =============================================================================

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using UnoX.Core;

namespace UnoX.Net
{
    /// <summary>Tipos cliente -&gt; servidor.</summary>
    public static class C2S
    {
        public const string Hello = "hello";
        public const string CreateRoom = "create_room";
        public const string JoinRoom = "join_room";
        public const string Leave = "leave";
        public const string SetReady = "set_ready";
        public const string StartGame = "start_game";
        public const string Reconnect = "reconnect";
        public const string PlayCard = "play_card";
        public const string Draw = "draw";
        public const string Pass = "pass";
        public const string CallUno = "call_uno";
        public const string CatchUno = "catch_uno";
        public const string Challenge = "challenge";
        public const string Emote = "emote";
        public const string Ping = "ping";
        public const string Resync = "resync";
    }

    /// <summary>Tipos servidor -&gt; cliente.</summary>
    public static class S2C
    {
        public const string Welcome = "welcome";
        public const string LobbyState = "lobby_state";
        public const string GameStart = "game_start";
        public const string Event = "ev";
        public const string Batch = "batch";
        public const string Snapshot = "snapshot";
        public const string Error = "error";
        public const string Pong = "pong";
        public const string PeerState = "peer_state";
        public const string RoomClosed = "room_closed";
        public const string Emote = "emote";
    }

    /// <summary>Sobre común. Todo mensaje lleva "t".</summary>
    [Serializable]
    public class Envelope
    {
        public string t;
    }

    [Serializable]
    public class WelcomeMsg
    {
        public string t;
        public int protocol;
        public string playerId;
        public string reconnectToken;
        public long serverTime;
        public int heartbeatMs;
        public bool reconnected;
    }

    [Serializable]
    public class LobbyPlayer
    {
        public string id;
        public string name;
        public string avatarId;
        public bool ready;
        public bool connected;
    }

    [Serializable]
    public class LobbyRoom
    {
        public string code;
        public string hostId;
        public string phase;
        public int maxPlayers;
        public RoomRules rules;
        public List<LobbyPlayer> players;
    }

    [Serializable]
    public class LobbyStateMsg
    {
        public string t;
        public LobbyRoom room;
    }

    [Serializable]
    public class GameStartMsg
    {
        public string t;
        public GameSnapshot game;
    }

    [Serializable]
    public class SnapshotMsg
    {
        public string t;
        public GameSnapshot game;
    }

    [Serializable]
    public class EventMsg
    {
        public string t;
        public GameEvent e;
    }

    [Serializable]
    public class BatchMsg
    {
        public string t;
        public List<GameEvent> e;
    }

    [Serializable]
    public class ErrorMsg
    {
        public string t;
        public string code;
        public string message;
        public string @ref;
    }

    [Serializable]
    public class PongMsg
    {
        public string t;
        public long c;
        public long serverTime;
    }

    [Serializable]
    public class PeerStateMsg
    {
        public string t;
        public string playerId;
        public bool connected;
        public bool left;
    }

    [Serializable]
    public class RoomClosedMsg
    {
        public string t;
        public string code;
        public string reason;
    }

    [Serializable]
    public class EmoteMsg
    {
        public string t;
        public string playerId;
        public string id;
    }

    /// <summary>
    /// Evento de juego. Es un DTO "abierto": el servidor añade campos según el
    /// tipo de evento y el cliente lee sólo los que le interesan. Con
    /// NullValueHandling.Ignore los campos ausentes no ensucian el JSON.
    /// </summary>
    [Serializable]
    public class GameEvent
    {
        public int seq;
        public string type;
        public long at;

        public int seat;
        public int turnIndex;
        public int direction;
        public int amount;
        public int count;
        public int bySeat = -1;
        public int sourceSeat = -1;
        public int targetSeat = -1;
        public int otherSeat = -1;
        public int playerSeat = -1;

        public bool stacked;
        public bool accepted;
        public bool bluffed;
        public bool timeout;
        public bool preDeclared;
        public bool final;
        public bool fromChallenge;

        public PublicCard? card;
        public CardColor? chosenColor;
        public CardColor? color;

        /// <summary>Sólo presente si el evento es tuyo. Nunca llega el del rival.</summary>
        public List<PublicCard> cards;
        public List<PublicCard> revealed;

        public List<int> handSizes;
        public int? handSize;
        public List<int> chainSeats;

        public int winnerSeat = -1;
        public string winnerId;
        public int gained;
        public int targetScore;
        public long deadline;
        public int playableCount;
        public int stackableCount;
        public int pendingAmount;

        public List<ScoreEntry> scores;
        public List<RoundBreakdown> breakdown;
        public string by;

        public bool IsMine(int mySeat) => seat == mySeat;
    }

    [Serializable]
    public class ScoreEntry
    {
        public int seat;
        public string id;
        public int score;
    }

    [Serializable]
    public class RoundBreakdown
    {
        public int seat;
        public int points;
        public List<PublicCard> cards;
    }

    /// <summary>Serializador compartido. Una sola instancia: crearla es caro.</summary>
    public static class Json
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            DefaultValueHandling = DefaultValueHandling.Include,
            MissingMemberHandling = MissingMemberHandling.Ignore
        };

        public static string ToJson(object obj) => JsonConvert.SerializeObject(obj, Settings);
        public static T FromJson<T>(string json) => JsonConvert.DeserializeObject<T>(json, Settings);
    }
}
