namespace CorpMsg.SupportClasses
{
    public class Result<T>
    {
        public bool IsSuccess { get; }
        public T Data { get; }
        public ApiError Error { get; }

        private Result(bool isSuccess, T data, ApiError error)
        {
            IsSuccess = isSuccess;
            Data = data;
            Error = error;
        }

        public static Result<T> Success(T data) => new(true, data, null);
        public static Result<T> Failure(ApiError error) => new(false, default, error);
    }

    public class ApiError
    {
        public string Message { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public string? Details { get; set; }
    }
}