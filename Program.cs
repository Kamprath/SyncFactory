using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Newtonsoft.Json;
using Renci.SshNet;
using System.Text;
using Renci.SshNet.Security;
using System.Globalization;

class Program
{
    static void Main()
    {
        string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "config.json");
        Config config;

        if (!File.Exists(configPath))
        {
            Console.WriteLine("No config found.");
            config = SetupProcess(configPath);
        }
        else
        {
            config = JsonConvert.DeserializeObject<Config>(File.ReadAllText(configPath));
        }

        // if the save file doesn't exist, call SetupProcess()
        if (!File.Exists(config.SaveFilePath))
        {
            SetupProcess(configPath);
        }

        try
        {
            using (var sftp = new SftpClient(GetConnectionInfo(config)))
            {
                DownloadProcess(sftp, config);
                var startingLastModified = File.GetLastWriteTime(config.SaveFilePath);

                RunGame();

                var endingLastModified = File.GetLastWriteTime(config.SaveFilePath);

                if (endingLastModified != startingLastModified)
                {
                    UploadProcess(sftp, config);
                } else
                {
                    Console.WriteLine($"No changes detected to save file (Start: {startingLastModified.ToString()}, End: {endingLastModified.ToString()}. Skipping upload.");
                }
            }
        } catch (Exception e)
        {
            Console.WriteLine("ERROR:\n" + e.Message);

            Console.WriteLine("\nPress any key to exit...");
            Console.Read();
        }
    }

      static Config SetupProcess(string configPath)
    {
        Config config = new Config
        {
            Host = "",
            Username = "",
            SaveFilePath = ""
        };

        string saveGamesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");
        string firstDirectory = Directory.GetDirectories(saveGamesDirectory).FirstOrDefault();

        if (firstDirectory == null)
        {
            Console.WriteLine("Error: No save game folder found. Make sure Satisfactory is installed.");
            Environment.Exit(0);
        }

        // create config directory
        var configDirectory = Path.GetDirectoryName(configPath);
        if (!Directory.Exists(Path.GetDirectoryName(configPath)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath));
        }

        Console.WriteLine("Enter SFTP Host: ");
        config.Host = Console.ReadLine();

        Console.WriteLine("Enter SFTP Username: ");
        config.Username = Console.ReadLine();

        Console.WriteLine("Paste SSH Private Key: \n");
        StringBuilder input = new StringBuilder();
        string line;
        while ((line = Console.ReadLine()) != "")
        {
            input.AppendLine(line);
        }

        // write value of key to %AppData%\FactoryGame\SyncFactory\key
        File.WriteAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key"), input.ToString());

        using (var sftp = new SftpClient(GetConnectionInfo(config)))
        {
            sftp.Connect();

            Console.WriteLine("Do you want to download an existing world file or upload your own? (Enter 'download' or 'upload')");
            string choice = Console.ReadLine();

            if (choice == "download")
            {
                Console.WriteLine("List of save files on the server:");

                 //List all save files on the server and prompt the user for their choice
                var files = sftp.ListDirectory("/");

                foreach (var file in files)
                {
                    Console.WriteLine(file.Name);
                }

                Console.WriteLine("Enter the number of the save file you want to download:");
                int selectedFileIndex = int.Parse(Console.ReadLine());

                 //Download the selected save file to the first directory in the local save games folder
                string selectedFile = files.ElementAt(selectedFileIndex - 1).Name;
                string saveFilePath = Path.Combine(firstDirectory, Path.GetFileName(selectedFile));

                if (File.Exists(saveFilePath))
                {
                    Console.WriteLine("A save file with that name already exists. Do you want to overwrite it? (Enter 'yes' or 'no')");
                    string overwriteChoice = Console.ReadLine();

                    if (overwriteChoice == "yes")
                    {
                        File.Delete(saveFilePath);
                    }
                    else
                    {
                        Console.WriteLine("Aborting download.");
                        sftp.Disconnect();
                        return config;
                    }
                }

                sftp.DownloadFile(selectedFile, File.OpenWrite(saveFilePath));

                 //Store the path to the downloaded save file in the config
                config.SaveFilePath = saveFilePath;
            }
            else
            {
                Console.WriteLine("List of .sav files in the local save games folder:");
                string[] savFiles = Directory.GetFiles(firstDirectory, "*.sav");

                for (int i = 0; i < savFiles.Length; i++)
                {
                    Console.WriteLine($"{i + 1}. {Path.GetFileName(savFiles[i])}");
                }

                Console.WriteLine("Enter the number of the .sav file you want to upload:");
                int selectedSavFileIndex = int.Parse(Console.ReadLine());

                 //Get the path of the selected .sav file
                string selectedSavFile = savFiles[selectedSavFileIndex - 1];
                string saveFilePath = Path.Combine(firstDirectory, Path.GetFileName(selectedSavFile));

                //sftp.UploadFile(File.OpenRead(selectedSavFile), "/saves/test");

                 //Store the path to the uploaded .sav file in the config
                config.SaveFilePath = saveFilePath;
            }

            sftp.Disconnect();
        }

        // Write the config to the config file
        string configJson = JsonConvert.SerializeObject(config);
        File.WriteAllText(configPath, configJson);

        return config;
    }
      static void DownloadProcess(SftpClient sftp, Config config)
    {
        sftp.Connect();

        var saveName = Path.GetFileNameWithoutExtension(config.SaveFilePath);
        var saveFileName = Path.GetFileName(config.SaveFilePath);
        var localWriteTime = File.GetLastWriteTime(config.SaveFilePath);

        if (!sftp.Exists($"/saves/{saveName}"))
        {
            Console.WriteLine($"No save files found on server for {saveName}");
            return;
        }

        var sftpFiles = sftp.ListDirectory($"/saves/{saveName}").ToList();
        if (sftpFiles.Count > 0)
        {
            var remoteFile = sftpFiles.OrderByDescending(f => f.Name).First();
            
            DateTime remoteWriteTime = DateTime.ParseExact(remoteFile.Name.Replace(".sav", ""), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            
            if (remoteWriteTime > localWriteTime)
            {
                string backupFilePath = config.SaveFilePath + ".backup";
                File.Move(config.SaveFilePath, backupFilePath);

                var saveFileOutput = File.OpenWrite(config.SaveFilePath);
                sftp.DownloadFile(remoteFile.FullName, saveFileOutput);

                var localTime = remoteWriteTime.ToLocalTime();
                Console.WriteLine($"\nDownloaded latest version of {saveName} (Last updated {localTime.Month}/{localTime.Day}/{localTime.Year} {localTime.Hour}:{localTime.Minute})");
            } else
            {
                Console.WriteLine($"You have the most recent version of {saveName}. Skipping download.");
            }
        } else
        {
            Console.WriteLine($"No save files found on server for {saveName}");
        }

        sftp.Disconnect();
    }

    static void RunGame()
    {
        var appId = "526870";
        var gameProcessName = "FactoryGame";

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = $"/c start steam://rungameid/{appId}",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            // Wait for the game process to start
            Process gameProcess = null;
            while (gameProcess == null)
            {
                gameProcess = Process.GetProcessesByName(gameProcessName).FirstOrDefault();
                Thread.Sleep(1000); // Check every second
            }

            Console.WriteLine("Satisfactory started.");

            // Wait for the game to exit
            gameProcess.WaitForExit();
            Console.WriteLine("Game exited.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"An error occurred: {ex.Message}");
        }
    }

    static void UploadProcess(SftpClient sftp, Config config)
    {
        // get name of file from config.SaveFilePath
        string fileName = Path.GetFileName(config.SaveFilePath);
        string saveName = Path.GetFileNameWithoutExtension(config.SaveFilePath);

        // get timestamp of file last modified time
        DateTime localWriteTime = File.GetLastWriteTime(config.SaveFilePath);
        string timestring = localWriteTime.ToString("yyyy-MM-dd_HH-mm-ss");

        if (!sftp.Exists($"/saves/{saveName}"))
        {
            sftp.CreateDirectory($"/saves/{saveName}");
        }

        var sftpFiles = sftp.ListDirectory($"/saves/{saveName}").ToList();
        if (sftpFiles.Count > 0)
        {
            var remoteFile = sftpFiles.OrderByDescending(f => f.Name).First();

            DateTime remoteWriteTime = DateTime.ParseExact(remoteFile.Name.Replace(".sav", ""), "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

            if (localWriteTime < remoteWriteTime)
            {
                Console.WriteLine($"Uh oh... You have an older version of {saveName}. Skipping upload.");
                string backupFilePath = config.SaveFilePath + ".backup";

                if (File.Exists(backupFilePath))
                {
                    Console.WriteLine("Deleted previous backup.");
                    File.Delete(backupFilePath);
                }

                File.Copy(config.SaveFilePath, backupFilePath);
                Console.WriteLine($"Your local version has been backed up at {backupFilePath}.");

                return;
            }
        }

        Console.Write($"\nUploading {saveName}... ");
        sftp.UploadFile(File.OpenRead(config.SaveFilePath), $"/saves/{saveName}/{timestring}.sav");

        Console.Write("Done!");
    }

    static ConnectionInfo? GetConnectionInfo(Config config)
    {
        // read private key
        string keyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "SyncFactory", "key");

        // check if key exists
        if (!File.Exists(keyPath))
        {
            Console.WriteLine("ERROR: No key file found at " + keyPath);
            return null;
        }

        var stream = new FileStream(keyPath.ToString(), FileMode.Open, FileAccess.Read);
        var file = new PrivateKeyFile(stream);
        var authMethod = new PrivateKeyAuthenticationMethod(config.Username, file);

        return new ConnectionInfo(config.Host, config.Username, authMethod);
    }
}

class Config
{
    public string Host { get; set; }
    public string Username { get; set; }
    public string SaveFilePath { get; set; }
}
