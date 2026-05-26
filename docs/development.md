# Developer Guide

## Prerequisites

Install [.NET](https://dotnet.microsoft.com/en-us/download)

## Development Setup

1. Add your Google Application Credentials JSON file to the project root directory.

2. Create `appsettings.Development.json` by copying `appsettings.json` and put your variables to there.

3. Update the `Credentials` value in `appsettings.Development.json` to point to the credentials file path.

   Alternatively, you can provide the encoded JSON content directly in the `Credentials` setting.

4. Run the application:

```bash
dotnet run
```

## Available Endpoints

Once the application is running, the following endpoints will be available:

```text
http://localhost:5268/summary?token=
http://localhost:5268/opportunities?token=
http://localhost:5268/opportunities?publisher=Ashmole%20Trust&district=Barnet&token=
```