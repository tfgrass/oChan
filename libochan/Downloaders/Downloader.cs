using System;

namespace oChan.Downloaders
{
public abstract class Downloader
{
    public string? Url { get; set; }  // Make it nullable
    public string Progress { get; set; } = "0/0";
    public string Status { get; set; } = "Idle";

    // Abstract method that must be implemented by child classes
    public abstract bool CanHandle(string url);

    // Abstract method to start the download process
    public abstract void StartDownload();
   
   
  }    
}
