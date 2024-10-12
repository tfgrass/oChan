namespace oChan.Boards.EightKun;

using System;
using System.Net.Http;
using oChan.Boards.Base;
using oChan.Interfaces;
using Serilog;


public class EightKunImageBoard : BaseImageBoard
{
    public override string Name => "8kun";
    public override string NiceName => "8kun";
    public override Uri BaseUri => new Uri("https://8kun.top");

    public override bool CanHandle(Uri uri)
    {
        bool canHandle = base.CanHandle(uri);
        Log.Debug("EightKunImageBoard CanHandle({Uri}) returned {Result}", uri, canHandle);
        return canHandle;
    }

    public override IBoard GetBoard(Uri boardUri)
    {
        if (boardUri == null)
        {
            Log.Error("Board URI is null.");
            throw new ArgumentNullException(nameof(boardUri));
        }

        Log.Information("Creating EightKunBoard for URI: {BoardUri}", boardUri);
        return new EightKunBoard(this, boardUri);
    }

    public override IThread GetThread(Uri threadUri)
    {
        if (threadUri == null)
        {
            Log.Error("Thread URI is null.");
            throw new ArgumentNullException(nameof(threadUri));
        }

        Log.Information("Creating EightKunThread for URI: {ThreadUri}", threadUri);

        string boardPath = threadUri.AbsolutePath.Split("/res")[0];
        Uri boardUri = new Uri($"https://8kun.top{boardPath}");
        EightKunBoard board = (EightKunBoard)GetBoard(boardUri);

        return new EightKunThread(board, threadUri);
    }

    public override HttpClient GetHttpClient()
    {
        HttpClient client = base.GetHttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/85.0.4183.121 Safari/537.36");
        Log.Debug("Configured HttpClient for EightKunImageBoard.");
        return client;
    }
}
