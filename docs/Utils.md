# Desktop formatting utilities

`oChan.Utils` belongs to the desktop reference client, in [Desktop/Utils.cs](../Desktop/Utils.cs). It is not part of the `libochan` package. Library consumers receive numeric values and format them for their own interface.

`ToHumanReadableSize(long bytes)` formats a byte count for display:

```csharp
long fileSize = 12345678;
string readableSize = Utils.ToHumanReadableSize(fileSize);
Console.WriteLine(readableSize); // "11.77 MB"
```
