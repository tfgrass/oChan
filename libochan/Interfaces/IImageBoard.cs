// File Path: ./Interfaces/IImageBoard.cs

using System;
using System.Net.Http;

namespace oChan.Interfaces
{
    public interface IImageBoard
    {
        string Name { get; }
        string NiceName { get; }
        Uri BaseUri { get; }

        bool CanHandle(Uri uri);

        bool IsThreadUri(Uri uri);
        bool IsBoardUri(Uri uri);

        IBoard GetBoard(Uri boardUri);
        IThread GetThread(Uri threadUri);

        HttpClient GetHttpClient();
    }
}
