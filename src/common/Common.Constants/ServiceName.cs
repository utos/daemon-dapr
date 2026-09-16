namespace Common.Constants;

public static class ServiceName
{
    public static class Database
    {
        /// <summary>
        /// The one database: Dapr's actor state, the read models, the event streams and the
        /// output streams. Also the connection-string name the daemon reads
        /// (<c>ConnectionStrings__utos</c>) and the Aspire resource name.
        /// </summary>
        public const string Utos = "utos";
    }

    public static class App
    {
        public const string MainApi = "main-api";
    }
}
