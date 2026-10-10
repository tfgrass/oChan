// File Path: ./Interfaces/IImageBoard.cs

using System;
using System.Net.Http;
using Serilog;

namespace oChan.Interfaces
{
    public interface IImageBoard
    {
        string Name { get; }
        string NiceName { get; }
        Uri BaseUri { get; }

        /// <summary>Caller-owned diagnostic logger; custom providers are silent by default.</summary>
        ILogger Logger => Serilog.Core.Logger.None;

        bool CanHandle(Uri uri);

        bool IsThreadUri(Uri uri);
        bool IsBoardUri(Uri uri);

        IBoard GetBoard(Uri boardUri);
        IThread GetThread(Uri threadUri);

        HttpClient GetHttpClient();
    }
}
