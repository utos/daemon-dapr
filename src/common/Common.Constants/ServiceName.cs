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

    public static class Storage
    {
        /// <summary>
        /// S3-compatible object storage for binary data — the bytes a blob stands for, where the
        /// database holds only the handle (<c>api/docs/binary-data.md</c>). RustFS in the Aspire
        /// model because it speaks the S3 API in a container; any S3 implementation in a
        /// deployment. Also the connection-string name the daemon reads
        /// (<c>ConnectionStrings__blobs</c>) and the Aspire resource name.
        /// </summary>
        public const string Blobs = "blobs";
    }

    public static class App
    {
        public const string MainApi = "main-api";
    }
}
