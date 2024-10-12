# Utils Class Documentation

The `Utils` class contains utility methods used across the application.

## Methods:

### `ToHumanReadableSize(long bytes)`
Converts a file size in bytes to a human-readable format (e.g., KB, MB, GB).

- **Parameters**: 
  - `bytes`: The size in bytes.
- **Returns**: A human-readable string (e.g., "1.23 MB").

## Example

```csharp
long fileSize = 12345678;
string readableSize = Utils.ToHumanReadableSize(fileSize);
Console.WriteLine(readableSize);  // Output: "11.77 MB"
```
