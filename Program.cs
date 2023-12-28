using System.Diagnostics;
using Newtonsoft.Json;
using Renci.SshNet;
using System.Text;
using System.Globalization;
using Renci.SshNet.Sftp;

class Program
{
    const string VERSION = "1.0.2";
    const string APP_ID = "526870";
    const string GAME_PROCESS_NAME = "FactoryGame";

    static string configPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "config.json");
        }
    }

    static string saveGamesPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        }
    }

    static string privateKeyPath
    {
        get
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key");
        }
    }

    static void WriteLineColor(string message, ConsoleColor color)
    {
        WriteColor(message + "\n", color);
    }

    static void WriteColor(string message, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.Write(message);
        Console.ResetColor();
    }

    static void LogMessage(string message, ConsoleColor? color = null)
    {
        WriteColor($"[{DateTime.Now.ToString("HH:mm")}] ", ConsoleColor.Yellow);

        if (color != null)
        {
            Console.ForegroundColor = color.Value;
        }

        Console.Write(message);

        if (color != null)
        {
            Console.ResetColor();
        }

    }

    static void LogError(string message)
    {
        LogMessage($"[ERROR] {message}\n", ConsoleColor.Red);
    }

    static void Main()
    {
        WriteLineColor($"===================\nSyncFactory v{VERSION}\n===================\n", ConsoleColor.Green);

        Config? config = GetConfig();

        if (config == null)
        {
            config = SetUpConfig(SetUpAuth());
        }

        try
        {
            // run setup again if save file was deleted
            if (!File.Exists(GetLatestAutosavePath(config.SaveName)))
            {
                if (SetUpConfig(config) == null)
                {
                    LogError("Failed to set up config.");
                    return;
                }
            } else
            {
                Download(config);
            }

            var startingLastModified = File.GetLastWriteTime(GetLatestAutosavePath(config.SaveName));

            RunGame();

            var autosavePath = GetLatestAutosavePath(config.SaveName);

            if (autosavePath != null)
            {
                if (File.GetLastWriteTime(autosavePath) > startingLastModified)
                {
                    Upload(config);
                } else
                {
                    LogMessage($"Save file hasn't changed. Skipping upload.\n");
                }
            }

            Console.WriteLine("\nExiting...");
            Thread.Sleep(2000);
        } catch (Exception e)
        {
            LogError(e.Message);

            Console.Write("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    static Config SetUpAuth()
    {
        Config config = new Config();

        // create config directory if it doesn't exist
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        Console.WriteLine("Welcome to SyncFactory! This program will help you set up your Satisfactory save file for syncing.\n");

        Console.Write("Enter SFTP Host: ");
        config.Host = Console.ReadLine();

        Console.Write("Enter SFTP Username: ");
        config.Username = Console.ReadLine();

        Console.WriteLine($"Paste SSH Private Key for {config.Username}@{config.Host}: \n");

        StringBuilder input = new StringBuilder();
        string line;
        while (!string.IsNullOrWhiteSpace(line = Console.ReadLine()))
        {
            input.AppendLine(line);
        }

        // write value of key to %AppData%\FactoryGame\SyncFactory\key
        File.WriteAllText(privateKeyPath, input.ToString());

        return config;
    }

    static Config? GetConfig()
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
        } catch (Exception e)
        {
            LogError($"Invalid config file: {e.Message}");
            return null;
        }
    }

    static Config? SetUpConfig(Config? existingConfig = null)
    {
        Config config = existingConfig ?? new Config
        {
            Host = "",
            Username = "",
            SaveName = ""
        };

        string firstDirectory = Directory.GetDirectories(saveGamesPath).FirstOrDefault();

        if (firstDirectory == null)
        {
            LogError("No save game folder found. Make sure Satisfactory is installed.");
            Environment.Exit(0);
        }

        // create config directory
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        Console.WriteLine("Do you want to download an existing world file or upload your own? (Enter 'download' or 'upload'):");
        string choice = Console.ReadLine();

        if (choice == "download")
        {
            using (var sftp = Connect(config.Host, config.Username))
            {
                if (sftp == null)
                {
                    LogError("Failed to connect to server.");
                    return null;
                }

                WriteLineColor("\n CLOUD SAVES:", ConsoleColor.Yellow);
                Console.WriteLine(" --------------------");

                //List all save files on the server and prompt the user for their choice
                var saves = sftp.ListDirectory("/saves").Where(file => file.Name != "." && file.Name != "..").ToList();

                var i = 1;
                foreach (var save in saves)
                {
                    WriteColor($" {i++}", ConsoleColor.Yellow);
                    Console.Write($". {save.Name}\n");
                }

                Console.WriteLine(" --------------------");

                Console.Write("\nEnter save number to download: ");
                int selectedFileIndex = int.Parse(Console.ReadLine());

                //Download the selected save file to the first directory in the local save games folder
                var selectedSave = saves.ElementAt(selectedFileIndex - 1);
                string saveFilePath = Path.Combine(firstDirectory, $"{selectedSave.Name}_autosave_0.sav");

                // Find files that begin with the selectedFolder name (delimited by _autosave*)
                string[] existingSaveFiles = Directory.GetFiles(firstDirectory, $"{selectedSave.Name}_autosave*.sav");

                if (existingSaveFiles.Length > 0)
                {
                    Console.WriteLine("\nA save file with that name already exists. Do you want to overwrite it? (Enter 'yes' or 'no'):");
                    string overwriteChoice = Console.ReadLine();

                    if (overwriteChoice == "yes")
                    {
                        // delete each file in existingSaveFiles
                        foreach (string file in existingSaveFiles)
                        {
                            File.Delete(file);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Aborting download.");
                        return config;
                    }
                }

                var latestSftpFile = GetLatestSftpSave(sftp, selectedSave.Name);
                DownloadSaveFile(sftp, selectedSave.Name, latestSftpFile.FullName, saveFilePath);

                config.SaveName = selectedSave.Name;
            }
        }
        else
        {
            WriteColor("\n LOCAL SAVES\n", ConsoleColor.Yellow);
            Console.WriteLine(" --------------------");

            string[] savFiles = Directory.GetFiles(firstDirectory, "*_autosave_*.sav");

            if (savFiles.Length == 0)
            {
                LogError("No save files found.");
                Environment.Exit(0);
            }
            // remove file path and "_autosave*" 
            string[] saveNames = savFiles.Select(f =>
            {
                int index = f.IndexOf("_autosave");
                return index >= 0 ? f.Substring(0, index) : f;
            }).Distinct().Select(f => Path.GetFileName(f)).ToArray();

            for (int i = 0; i < saveNames.Length; i++)
            {
                // remove the rest of the filename starting at _autosave* and print the name of the save
                WriteColor($" {i + 1}", ConsoleColor.Yellow);
                Console.Write($". {saveNames[i]}\n");
            }

            Console.WriteLine(" --------------------\n");

            Console.Write("Enter number of save to upload: ");
            int selectedSaveNameIndex = int.Parse(Console.ReadLine().Replace(".", ""));

            string selectedSaveName = saveNames[selectedSaveNameIndex - 1];
            string saveFilePath = Path.Combine(firstDirectory, GetLatestAutosavePath(selectedSaveName));

                //Store the path to the uploaded .sav file in the config
            config.SaveName = selectedSaveName;

            Upload(config);
        }

        return SaveConfig(config);
    }

    static Config? SaveConfig(Config config)
    {
        try
        {
            string configJson = JsonConvert.SerializeObject(config);
            File.WriteAllText(configPath, configJson);
        } catch (Exception e)
        {
            LogError("Failed to write to config file - {e.Message}\n");
            return null;
        }

        return config;
    }

    static void DownloadSaveFile(SftpClient sftp, string saveName, string remotePath, string localPath)
    {
        LogMessage($"Downloading latest version of {saveName}...");

        var fileStream = File.OpenWrite(localPath);
        sftp.DownloadFile(remotePath, fileStream);
        fileStream.Close();

        WriteColor($" Done\n", ConsoleColor.Green);
        Console.WriteLine($" Updated {localPath}");
    }

    static ISftpFile? GetLatestSftpSave(SftpClient sftp, string saveName)
    {
        var saveFiles = sftp.ListDirectory($"/saves/{saveName}").Where(file => file.Name != "." && file.Name != "..").ToList();

        if (saveFiles.Count == 0)
        {
            return null;
        }

        return saveFiles.OrderByDescending(f => f.Name).First();
    }

    static string? GetLatestAutosavePath(string saveName)
    {
        string saveDirectory = Directory.GetDirectories(saveGamesPath).FirstOrDefault();

        // get list of files in the saveDirectory starting with saveName
        var saveFiles = Directory.GetFiles(saveDirectory, saveName + "*_autosave_*.sav");

        if (saveFiles.Length == 0)
        {
            return null;
        }

        // get the one with the latest last write time
        var latestFile = saveFiles.OrderByDescending(f => File.GetLastWriteTime(f)).First();

        return latestFile;
    }

    static SftpClient? Connect(string host, string username)
    {
        var sftp = new SftpClient(GetConnectionInfo(host, username));

        LogMessage($"Connecting to {username}@{host}...");

        try
        {
            sftp.Connect();
            WriteColor(" Connected\n", ConsoleColor.Green);
            return sftp;
        } catch (Exception e)
        {
            WriteColor($" Failed\n Error: {e.Message}\n", ConsoleColor.Red);
            return null;
        }
    }
    static void Download(Config config)
    {
        var saveFilePath = GetLatestAutosavePath(config.SaveName);
        var localWriteTime = File.GetLastWriteTime(saveFilePath);

        using (var sftp = Connect(config.Host, config.Username))
        {
            if (sftp == null)
            {
                LogError("Failed to connect to SFTP server.");
                return;
            }

            if (!sftp.Exists($"/saves/{config.SaveName}"))
            {
                LogMessage($"No save files found on server for {config.SaveName}\n", ConsoleColor.Yellow);
                return;
            }

            var latestFile = GetLatestSftpSave(sftp, config.SaveName);

            if (latestFile != null)
            {
                DateTime remoteWriteTime = DateTime.ParseExact(latestFile.Name.Replace(".sav", ""), "yyMMdd-HHmmss", CultureInfo.InvariantCulture);
            
                if (remoteWriteTime > localWriteTime)
                {
                    DownloadSaveFile(sftp, config.SaveName, latestFile.FullName, saveFilePath);
                } else
                {
                    LogMessage($"You have the most recent version of {config.SaveName}.\n");
                }
            } else
            {
                LogMessage($"No save files found on server for {config.SaveName}\n", ConsoleColor.Yellow);
            }
        }
    }

    static void RunGame()
    {
        LogMessage("Starting Satisfactory...");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start steam://rungameid/{APP_ID}",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            // Wait for the game process to start
            Process gameProcess = null;
            while (gameProcess == null)
            {
                gameProcess = Process.GetProcessesByName(GAME_PROCESS_NAME).FirstOrDefault();
                Thread.Sleep(1000);
            }

            WriteColor(" Started\n", ConsoleColor.Green);

            // Wait for the game to exit
            gameProcess.WaitForExit();

            LogMessage("Game exited.\n");
        }
        catch (Exception ex)
        {
            WriteColor($" Failed.\n", ConsoleColor.Red);

            LogError(ex.Message);
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }

    static void Upload(Config config)
    {
        string saveName = config.SaveName;
        string latestSavePath = GetLatestAutosavePath(saveName);
        string latestSaveFileName = Path.GetFileName(latestSavePath);

        // get timestamp of file last modified time
        DateTime localWriteTime = File.GetLastWriteTime(latestSavePath);
        string timestring = localWriteTime.ToString("yyMMdd-HHmmss");

        using (var sftp = Connect(config.Host, config.Username))
        {
            if (sftp == null)
            {
                LogError("Failed to connect to server.");
                return;
            }

            if (!sftp.Exists($"/saves/{saveName}"))
            {
                sftp.CreateDirectory($"/saves/{saveName}");
            }

            var sftpFiles = sftp.ListDirectory($"/saves/{saveName}").Where(file => file.Name != "." && file.Name != "..").ToList();

            if (sftpFiles.Count > 0)
            {
                var remoteFile = sftpFiles.OrderByDescending(f => f.Name).First();

                DateTime remoteWriteTime = DateTime.ParseExact(remoteFile.Name.Replace(".sav", ""), "yyMMdd-HHmmss", CultureInfo.InvariantCulture);

                if (localWriteTime < remoteWriteTime)
                {
                    LogMessage($"Uh oh... You have an older version of {saveName}. Skipping upload.\n", ConsoleColor.Yellow);

                    string backupFilePath = latestSavePath + ".backup";

                    if (File.Exists(backupFilePath))
                    {
                        Console.WriteLine("Deleted previous backup.");
                        File.Delete(backupFilePath);
                    }

                    File.Copy(latestSavePath, backupFilePath);
                    Console.WriteLine($"Your local version will be overwritten next time you run SyncFactory. A backup has been created at {backupFilePath}.");

                    return;
                }
            }

            LogMessage($"Uploading {saveName}... ");
        
            try
            {
                sftp.UploadFile(File.OpenRead(latestSavePath), $"/saves/{saveName}/{timestring}.sav");
            } catch (Exception e)
            {
                LogError($"Failed to upload save file: {e.Message}");
                return;
            }   

            WriteColor("Done\n", ConsoleColor.Green);
        }
    }

    static ConnectionInfo? GetConnectionInfo(string host, string username)
    {
        // check if key exists
        if (!File.Exists(privateKeyPath))
        {
            Console.WriteLine("ERROR: No private key file found at " + privateKeyPath);
            return null;
        }

        try
        {
            var stream = new FileStream(privateKeyPath, FileMode.Open, FileAccess.Read);
            var file = new PrivateKeyFile(stream);
            var authMethod = new PrivateKeyAuthenticationMethod(username, file);

            return new ConnectionInfo(host, username, authMethod);
        }
        catch (Exception ex)
        {
            LogError($"Failed to read private key: {ex.Message}");
            return null;
        }
    }
}

class Config
{
    public string? Host { get; set; }
    public string? Username { get; set; }
    public string? SaveName { get; set; }
}
