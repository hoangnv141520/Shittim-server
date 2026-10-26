namespace Shittim.CLI
{
    public class Arguments
    {
        public static async Task RunServerAsync(
            bool update = false,
            bool console = false,
            long? id = null,
            bool restoreClient = false
        )
        {
            if (restoreClient)
            {
                Environment.ExitCode = Shittim_Server.Services.OfficialClientRestore.Run();
                return;
            }
            await GameServer.Run(update, console, id);
        }
    }
}
