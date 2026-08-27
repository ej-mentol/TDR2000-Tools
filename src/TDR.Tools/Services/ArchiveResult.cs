namespace TDR.Tools.Services
{
    /// <summary>
    /// Result Object for archive operations, encapsulating success status, structured log message, and affected items count.
    /// </summary>
    public readonly record struct ArchiveResult(bool IsSuccess, string Message, int AffectedCount = 0)
    {
        public static ArchiveResult Ok(string message, int affectedCount = 0) 
            => new(true, message, affectedCount);

        public static ArchiveResult Fail(string error) 
            => new(false, error, 0);

        public static implicit operator bool(ArchiveResult result) => result.IsSuccess;

        public override string ToString() => Message;
    }
}
