using System;
using System.Runtime.Serialization;

namespace MyPhotoWebApi.Helpers
{
    [Serializable]
    internal class MyPhotoException : Exception
    {
        public MyPhotoException()
        {
        }

        public MyPhotoException(string message, MyErrorCode errorCode) : base(message)
        {
            ErrorCode = errorCode;
        }

        public MyPhotoException(string message, Exception innerException) : base(message, innerException)
        {
        }

        protected MyPhotoException(SerializationInfo info, StreamingContext context) : base(info, context)
        {
        }

        public MyErrorCode ErrorCode { get; private set; }
    }
}