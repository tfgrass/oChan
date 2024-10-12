// File Path: ./Boards/Base/BaseImageBoard.cs

using System;
using System.Net.Http;
using oChan.Interfaces;
using Serilog;

namespace oChan.Boards.Base
{
    /// <summary>
    /// Base class for image boards, providing common functionality.
    /// </summary>
    public abstract class BaseImageBoard : IImageBoard
    {
        public abstract string Name { get; }
        public abstract string NiceName { get; }
        public abstract Uri BaseUri { get; }

        public abstract bool CanHandle(Uri uri);
        public abstract bool IsThreadUri(Uri uri);
        public abstract bool IsBoardUri(Uri uri);

        public abstract IBoard GetBoard(Uri boardUri);
        public abstract IThread GetThread(Uri threadUri);

        public virtual HttpClient GetHttpClient()
        {
            HttpClient client = new HttpClient();
            // Set common headers or configurations if needed
            Log.Debug("BaseImageBoard: Created HttpClient instance.");
            return client;
        }
    }
}
