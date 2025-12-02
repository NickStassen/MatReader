# MatReader

MatReader is a .NET library for reading MATLAB .mat annoation files into csv format. 

## Installation

Clone the repository:

```sh
git clone https://github.com/NickStassen/MatReader.git
```

Install .NET 10.0 SDK using the following command:

```sh
sudo apt-get install -y dotnet-sdk-10.0
```

## Usage

Run the command:

```sh
dotnet run -- [PATH_TO_DRONE_DETECTION_DATASET]/Drone-detection-dataset/Data/Video_V ./output_V
dotnet run -- [PATH_TO_DRONE_DETECTION_DATASET]/Drone-detection-dataset/Data/Video_IR ./output_IR
```

Now you should have the csv files ready to export to YOLO format.
