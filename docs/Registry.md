# Registry Class Documentation

The `Registry` class is responsible for managing and registering various imageboards. It allows oChan to handle different boards dynamically, based on the URL provided by the user.

## Key Responsibilities:
- **ImageBoard Registration**: Registers imageboards such as `FourChanImageBoard`.
- **URL Handling**: Determines the appropriate imageboard to handle a given URL.
- **Logging**: Uses `Serilog` for debugging and operational logging.

## Methods:

### `HandleUrl(string url)`
Attempts to match the provided URL to a registered imageboard and retrieves the relevant thread.

- **Parameters**: 
  - `url`: The URL of the thread to be handled.
- **Returns**: An instance of `IThread` if the URL matches a registered board, or `null` if no match is found.

### `ListImageBoards()`
Prints a list of all registered imageboards to the console.

## Example

```csharp
Registry registry = new Registry();
IThread? thread = registry.HandleUrl("https://boards.4chan.org/g/thread/123456");
if (thread != null)
{
    Console.WriteLine($"Thread ID: {thread.ThreadId}");
}
```
