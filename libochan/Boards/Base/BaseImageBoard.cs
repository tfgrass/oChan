namespace oChan.Boards.Base;

using System;
using System.Net.Http;
using oChan.Interfaces;
using Serilog;

public abstract class BaseImageBoard : IImageBoard
{
    public abstract string Name { get; }
    public abstract string NiceName { get; }
    public abstract Uri BaseUri { get; }

    public virtual bool CanHandle(Uri uri)
    {
        Log.Debug("Checking if {ImageBoard} can handle URI: {Uri}", Name, uri);
        return uri.Host.Contains(BaseUri.Host);
    }

    public abstract IBoard GetBoard(Uri boardUri);
    public abstract IThread GetThread(Uri threadUri);

    public virtual HttpClient GetHttpClient()
    {
        Log.Debug("Providing HttpClient for {ImageBoard}", Name);
        return new HttpClient(); // Configure as needed
    }
}



