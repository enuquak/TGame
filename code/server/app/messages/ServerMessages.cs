namespace TGame.Server;

internal static class ServerMessages
{
    public static MessageDispatcher Create(bool diagnosticsEnabled = false)
    {
        var messages = new MessageDispatcher();
        ProfileMessages.Register(messages);
        GMMessages.Register(messages);

        if (diagnosticsEnabled)
        {
            DiagnosticMessages.Register(messages);
        }

        return messages;
    }
}
