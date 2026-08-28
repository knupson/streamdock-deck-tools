namespace StreamDeckAudio
{
    internal static class DiscordAuth
    {
        // El client de StreamKit. Se usa sólo para probar el transporte: el
        // handshake no pide autorización, así que valida el pipe antes de que
        // exista ninguna app propia.
        internal const string StreamKitClientId = "207646673902501888";
    }
}
