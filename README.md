# oChan - cross plattform imageboard scraper 

oChan is a cross-platform imageboard downloader designed to handle multiple boards from platforms like 4chan. The application is built in C# and uses Avalonia for the GUI, supporting features like media downloads, bandwidth throttling, and parallel task management. 

## Features
- **Support for Multiple Boards**: oChan can manage and download from multiple imageboards with minimal setup.
- **Download Queue**: The downloader supports parallel downloads, with customizable bandwidth and thread handling.
- **Bandwidth Limiting**: Ensure that you don't exceed your internet limits with customizable bandwidth control.
- **Modular and Extendable**: Easily add new boards and features through the `Registry` system.

## Installation

1. Clone the repository:
    ```bash
    git clone https://github.com/yourusername/oChan.git
    cd oChan
    ```

2. Build the project (make sure .NET SDK is installed):
    ```bash
    dotnet build
    ```

3. Run the project:
    ```bash
    dotnet run
    ```

## Usage

oChan provides a GUI for downloading threads and media from various boards. Here's a general workflow:

1. Select the board and enter the thread URL.
2. Start the download and monitor the progress through the UI.
3. Access downloaded media in the configured output folder.

For more information on the API and underlying functionality, refer to the [documentation](docs/).

## Contributing

1. Fork the project.
2. Create a new feature branch.
3. Commit your changes.
4. Push to your branch.
5. Create a pull request.

## License

This project is licensed under the Artistic License 2.0. See the LICENSE file for more details.
