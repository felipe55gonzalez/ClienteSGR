using Microsoft.AspNetCore.SignalR.Client;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Collections.Concurrent;
using ClienteSGR.Models;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Net;

namespace ClienteSGR
{
    public class ReassemblyState
    {
        public Guid TransferId { get; set; }
        public MessageType Type { get; set; }
        public List<byte> Buffer { get; set; } = new List<byte>();
        public int ExpectedSequence { get; set; } = 0;
        public string? Filename { get; set; }
        public long? OriginalSize { get; set; }

        public ReassemblyState(Guid transferId, MessageType type)
        {
            TransferId = transferId;
            Type = type;
        }

        public bool AddFragment(DataBatch batch)
        {
            if (batch.Sequence == ExpectedSequence)
            {
                if (batch.Data != null && batch.Data.Length > 0)
                {
                    Buffer.AddRange(batch.Data);
                }
                ExpectedSequence++;
                return true;
            }
            return false;
        }

        public async Task<string?> SaveFileAsync()
        {
            if (Type != MessageType.FileFragment || string.IsNullOrWhiteSpace(Filename))
            {
                Console.WriteLine($"Error: Intentando guardar archivo para TransferId {TransferId} con tipo {Type} o sin nombre.");
                return null;
            }

            try
            {
                string outputFilename = Filename;
                char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
                string safeOutputFilename = string.Join("_", outputFilename.Split(invalidFileNameChars, StringSplitOptions.RemoveEmptyEntries)).Trim();

                if (string.IsNullOrWhiteSpace(safeOutputFilename))
                {
                    safeOutputFilename = $"{TransferId}_received_file_safe_name.bin";
                    Console.WriteLine($"Advertencia: Nombre de archivo inválido o vacío recibido ('{outputFilename}'). Usando nombre seguro: '{safeOutputFilename}'");
                }
                if (safeOutputFilename == "." || safeOutputFilename == "..") safeOutputFilename = $"{TransferId}_safe.bin";

                string outputPath = Path.Combine(Directory.GetCurrentDirectory(), safeOutputFilename);
                await File.WriteAllBytesAsync(outputPath, Buffer.ToArray());
                return outputPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error al guardar el archivo reensamblado para TransferId {TransferId}: {ex.Message}");
                return null;
            }
        }
    }

    public class AppSettings
    {
        public string ServerUrl { get; set; } = "http://localhost:5137/datahub";
        public bool AutoConfigureNetwork { get; set; } = false;
        public string? ConfiguredLocalIp { get; set; } = null;
        public List<string> NetworksToRoute { get; set; } = new List<string>();
        public string? ClientAlias { get; set; } = null;
        public string? DefaultPeerAlias { get; set; } = null;
    }

    class Program
    {
        static HubConnection? connection;
        static string? clientAlias;
        static IntPtr wintunAdapter = IntPtr.Zero;
        static IntPtr wintunSession = IntPtr.Zero;
        static CancellationTokenSource wintunCancelTokenSource = new CancellationTokenSource();
        private static ConcurrentDictionary<Guid, ReassemblyState> _activeTransfers = new ConcurrentDictionary<Guid, ReassemblyState>();
        static string? peerAlias;
        static long ipPacketSentCounter = 0;
        static readonly Guid IpTransferId = Guid.Parse("00000000-0000-0000-0000-FEEDFACEFEED");

        static bool autoConfigureNetwork = false;
        static string? configuredLocalIp;
        static string wintunSubnetMask = "255.255.255.0";
        static List<string> networksToRoute = new List<string>();
        static string? wintunInterfaceName;
        static int? wintunInterfaceIndex;
        static bool networkWasConfiguredByApp = false;

        private static string configFilePath = "Config.json";
        private static AppSettings currentSettings = new AppSettings();

        private static StreamWriter? logWriter;
        private static readonly object logLock = new object();
        private static string logFileName = $"ClienteSGR_Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        static async Task Main(string[] args)
        {
            SetupLogging();
            Log("Inicio de la aplicación.");
            LoadConfiguration();

            Log("Cliente SignalR SGR (Emulador de Transporte)");
            Log("-------------------------------------------");

            if (connection == null)
            {
                try
                {
                    connection = new HubConnectionBuilder()
                       .WithUrl(currentSettings.ServerUrl)
                       .AddMessagePackProtocol()
                       .Build();
                    Log($"HubConnection construido para URL: {currentSettings.ServerUrl}");

                    RegisterServerHandlers();
                    RegisterClosedHandler();
                }
                catch (Exception ex)
                {
                    Log($"FATAL: Error al construir HubConnection: {ex.Message}");
                    Console.WriteLine("Error fatal al iniciar. Presiona Enter para salir.");
                    Console.ReadLine();
                    CleanupLogging();
                    return;
                }
            }

            bool keepRunning = true;
            while (keepRunning)
            {
                if (wintunAdapter != IntPtr.Zero || networkWasConfiguredByApp)
                {
                    Log("Advertencia: Detectado estado activo previo. Limpiando...");
                    await CleanupWinTunAndNetwork();
                }

                Log("\n--- Menú Principal ---");
                Log("1. Iniciar Túnel");
                Log("2. Configurar Aplicación");
                Log("3. Reparar Red (Limpieza Forzada)");
                Log("4. Salir");
                Console.Write("Selecciona una opción: ");
                string? choice = Console.ReadLine()?.Trim();
                Log($"Opción seleccionada: {choice ?? "NULL"}");

                switch (choice)
                {
                    case "1":
                        await StartTunnelMode();
                        break;
                    case "2":
                        ConfigureMode();
                        break;
                    case "3":
                        await RepairNetworkConfiguration();
                        break;
                    case "4":
                        Log("Saliendo de la aplicación...");
                        keepRunning = false;
                        break;
                    default:
                        Log("Opción inválida. Inténtalo de nuevo.");
                        break;
                }
                await Task.Delay(50);
            }

            Log("Aplicación finalizada.");
            CleanupLogging();
        }

        static void SetupLogging()
        {
            try
            {
                logWriter = new StreamWriter(logFileName, append: true) { AutoFlush = true };
                Log($"Logging iniciado. Escribiendo en: {Path.GetFullPath(logFileName)}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FATAL: No se pudo abrir el archivo de log '{logFileName}': {ex.Message}");
                logWriter = null;
            }
        }

        static void Log(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
                Console.WriteLine(message);
                lock (logLock)
                {
                    logWriter?.WriteLine(logEntry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error interno de logging: {ex.Message}");

            }
        }

        static void LogInline(string message)
        {
            try
            {
                string logEntry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [INLINE] {message}";
                Console.Write(message);
                lock (logLock)
                {
                    logWriter?.WriteLine(logEntry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error interno de logging (inline): {ex.Message}");
            }
        }

        static void CleanupLogging()
        {
            Log("Finalizando logging.");
            lock (logLock)
            {
                logWriter?.Close();
                logWriter = null;
            }
        }

        static void LoadConfiguration()
        {
            Log($"Cargando configuración desde {configFilePath}...");
            if (File.Exists(configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(configFilePath);
                    var options = new JsonSerializerOptions
                    {
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
                    if (settings != null)
                    {
                        currentSettings = settings;
                        Log("Configuración cargada exitosamente.");
                    }
                    else
                    {
                        Log($"Error: El archivo {configFilePath} está vacío o corrupto. Usando/Guardando valores por defecto.");
                        currentSettings = new AppSettings();
                        SaveConfiguration();
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error al leer/deserializar {configFilePath}: {ex.Message}. Usando/Guardando valores por defecto.");
                    currentSettings = new AppSettings();
                    SaveConfiguration();
                }
            }
            else
            {
                Log($"Advertencia: Archivo {configFilePath} no encontrado. Creando uno con valores por defecto.");
                currentSettings = new AppSettings();
                SaveConfiguration();
            }

            autoConfigureNetwork = currentSettings.AutoConfigureNetwork;
            configuredLocalIp = currentSettings.ConfiguredLocalIp;
            networksToRoute = currentSettings.NetworksToRoute ?? new List<string>();
            clientAlias = currentSettings.ClientAlias;
            peerAlias = currentSettings.DefaultPeerAlias;

            Log($"ServerUrl: {currentSettings.ServerUrl}");
            Log($"AutoConfigureNetwork: {autoConfigureNetwork}");
            Log($"ConfiguredLocalIp: {configuredLocalIp ?? "(Ninguno)"}");
            Log($"NetworksToRoute Count: {networksToRoute.Count}");
            Log($"ClientAlias: {clientAlias ?? "(Ninguno)"}");
            Log($"DefaultPeerAlias: {peerAlias ?? "(Ninguno)"}");
        }

        static void SaveConfiguration()
        {
            Log($"Guardando configuración en {configFilePath}...");
            try
            {
                currentSettings.AutoConfigureNetwork = autoConfigureNetwork;
                currentSettings.ConfiguredLocalIp = configuredLocalIp;
                currentSettings.NetworksToRoute = networksToRoute ?? new List<string>();
                currentSettings.ClientAlias = clientAlias;
                currentSettings.DefaultPeerAlias = peerAlias;

                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(currentSettings, options);
                File.WriteAllText(configFilePath, json);
                Log("Configuración guardada.");
            }
            catch (Exception ex)
            {
                Log($"Error al guardar configuración en {configFilePath}: {ex.Message}");
            }
        }

        static void ConfigureMode()
        {
            bool keepConfiguring = true;
            while (keepConfiguring)
            {
                Console.Clear();
                Log("\n--- Menú Configuración de Aplicación ---");
                Log($" Alias Cliente              : {clientAlias ?? "(Establecer Manualmente o en JSON)"}");
                Log($" Peer VPN por Defecto       : {peerAlias ?? "(No establecido)"}");
                Log("--- Configuración de Red ---");
                Log($" Estado Config. Automática  : {(autoConfigureNetwork ? "Habilitada" : "Deshabilitada")}");
                Log($" IP Local WinTun Propuesta  : {configuredLocalIp ?? "(No establecida)"}");
                Log($" Máscara de Subred         : {wintunSubnetMask} (No modificable aquí)");
                Log($" Redes a Enrutar            :");
                if (networksToRoute.Count == 0) { Log("   (Ninguna)"); }
                else
                {
                    int routeIndex = 1;
                    foreach (var network in networksToRoute) { Log($"   {routeIndex++}. {network}"); }
                }
                Log("---------------------------------");
                Log("Opciones:");
                Log("  C - Cambiar/Establecer Alias Cliente Actual");
                Log("  P - Cambiar/Establecer Peer VPN por Defecto");
                Log("  T - Cambiar Estado de Configuración Automática de Red");
                Log("  I - Establecer/Cambiar IP Local Propuesta (para modo automático)");
                Log("  A - Añadir Red a Enrutar (para modo automático)");
                Log("  E - Eliminar Red a Enrutar");
                Log("  V - Volver al Menú Principal");
                Console.Write("Selecciona una opción: ");

                string? choice = Console.ReadLine()?.Trim().ToUpper();
                Log($"Opción Configuración: {choice ?? "NULL"}");
                Console.WriteLine();
                bool configChanged = false;

                switch (choice)
                {
                    case "C":
                        Log($"Alias actual: {clientAlias ?? "Ninguno"}");
                        Console.Write("Introduce el nuevo alias para este cliente: ");
                        string? newAlias = Console.ReadLine()?.Trim();
                        if (!string.IsNullOrWhiteSpace(newAlias))
                        {
                            if (newAlias != clientAlias)
                            {
                                clientAlias = newAlias;
                                Log($"Alias de cliente cambiado a: '{clientAlias}'. Se usará la próxima vez que inicies el túnel.");
                                configChanged = true;
                            }
                        }
                        else
                        {
                            Log("Entrada inválida. Alias no cambiado.");
                        }
                        break;

                    case "P":
                        Log($"Peer VPN por defecto actual: {peerAlias ?? "Ninguno"}");
                        Console.Write("Introduce el alias del peer VPN por defecto: ");
                        string? newPeer = Console.ReadLine()?.Trim();
                        if (newPeer != peerAlias)
                        {
                            peerAlias = string.IsNullOrWhiteSpace(newPeer) ? null : newPeer;
                            Log($"Peer VPN por defecto cambiado a: '{peerAlias ?? "Ninguno"}'.");
                            configChanged = true;
                        }
                        break;

                    case "T":
                        autoConfigureNetwork = !autoConfigureNetwork;
                        configChanged = true;
                        Log($"Configuración automática de red ahora está {(autoConfigureNetwork ? "Habilitada" : "Deshabilitada")}.");
                        if (autoConfigureNetwork && string.IsNullOrWhiteSpace(configuredLocalIp)) { Log("Advertencia: Config. Auto habilitada sin IP local configurada."); }
                        if (autoConfigureNetwork && networksToRoute.Count == 0) { Log("Advertencia: Config. Auto habilitada sin redes a enrutar configuradas."); }
                        break;

                    case "I":
                        Console.Write($"Introduce la IP local (Actual: {configuredLocalIp ?? "Ninguna"}): ");
                        string? inputIp = Console.ReadLine()?.Trim();
                        if (IPAddress.TryParse(inputIp, out _))
                        {
                            if (configuredLocalIp != inputIp)
                            {
                                configuredLocalIp = inputIp;
                                Log($"IP local propuesta establecida a: {configuredLocalIp}");
                                configChanged = true;
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(inputIp)) { Log("Error: Formato de dirección IP inválido."); }
                        else
                        {
                            if (configuredLocalIp != null)
                            {
                                configuredLocalIp = null;
                                Log("IP local propuesta eliminada.");
                                configChanged = true;
                            }
                        }
                        break;

                    case "A":
                        Console.Write("Introduce la red a enrutar en formato CIDR (Ej: 192.168.100.0/24): ");
                        string? inputNetwork = Console.ReadLine()?.Trim();
                        if (!string.IsNullOrWhiteSpace(inputNetwork) && ParseCidr(inputNetwork, out _, out _))
                        {
                            if (!networksToRoute.Contains(inputNetwork, StringComparer.OrdinalIgnoreCase))
                            {
                                networksToRoute.Add(inputNetwork);
                                networksToRoute.Sort();
                                Log($"Red '{inputNetwork}' añadida a la lista.");
                                configChanged = true;
                            }
                            else { Log("Error: Esa red ya está en la lista."); }
                        }
                        else if (!string.IsNullOrWhiteSpace(inputNetwork)) { Log("Error: Formato de red CIDR inválido o no soportado."); }
                        break;

                    case "E":
                        if (networksToRoute.Count == 0) { Log("No hay redes en la lista para eliminar."); break; }
                        Log("Redes a enrutar actualmente:");
                        for (int i = 0; i < networksToRoute.Count; i++) { Log($"  {i + 1}. {networksToRoute[i]}"); }
                        Console.Write($"Introduce el número de la red a eliminar (1-{networksToRoute.Count}): ");
                        if (int.TryParse(Console.ReadLine(), out int indexToRemove) && indexToRemove >= 1 && indexToRemove <= networksToRoute.Count)
                        {
                            string removedNetwork = networksToRoute[indexToRemove - 1];
                            networksToRoute.RemoveAt(indexToRemove - 1);
                            Log($"Red '{removedNetwork}' eliminada de la lista.");
                            configChanged = true;
                        }
                        else { Log("Error: Número inválido."); }
                        break;

                    case "V":
                        keepConfiguring = false;
                        break;
                    default:
                        Log("Opción inválida.");
                        break;
                }
                if (configChanged) { SaveConfiguration(); }
                if (keepConfiguring) { Console.WriteLine("\nPresiona Enter para continuar..."); Console.ReadLine(); }
            }
            Console.Clear();
        }

        static async Task RepairNetworkConfiguration()
        {
            Console.Clear();
            Log("\n--- Reparar/Limpiar Configuración de Red ---");
            Log("Esta opción intentará eliminar las rutas de red que están");
            Log("definidas actualmente en la lista 'Redes a Enrutar' (Menú 2).");
            Log("Es útil si la aplicación se cerró inesperadamente.");
            Log("\n¡ADVERTENCIA! Esto ejecutará comandos 'route delete'.");
            Log("Asegúrate de que las redes listadas en 'Configurar Red'");
            Log("son las que quieres intentar eliminar.");
            Log("\nSe requieren permisos de Administrador.");
            Console.Write("\n¿Deseas continuar con la limpieza forzada? (S/N): ");
            string? confirm = Console.ReadLine()?.Trim().ToUpper();
            Log($"Confirmación de reparación: {confirm ?? "NULL"}");

            if (confirm != "S")
            {
                Log("Operación de reparación cancelada.");
                await Task.Delay(1500); Console.Clear(); return;
            }

            if (networksToRoute.Count == 0)
            {
                Log("No hay redes definidas en 'Redes a Enrutar'. No se intentará eliminar ninguna ruta.");
                await Task.Delay(2500); Console.Clear(); return;
            }

            Log("Iniciando intento de eliminación de rutas...");
            int successCount = 0; int failCount = 0;
            var routesToAttemptDelete = new List<string>(networksToRoute);

            foreach (var cidrNetwork in routesToAttemptDelete)
            {
                Log($"--- Procesando: {cidrNetwork} ---");
                if (ParseCidr(cidrNetwork, out string networkAddr, out string networkMask))
                {
                    string args1 = $"delete {networkAddr}";
                    Log($"Ejecutando: route {args1}");
                    var result1 = ExecuteCommand("route", args1);
                    await Task.Delay(100);

                    if (result1.success || result1.error.Contains("not found") || result1.error.Contains("no se encuentra"))
                    {
                        if (result1.success) Log("Éxito (o no encontrada).");
                        successCount++;
                    }
                    else
                    {
                        string args2 = $"delete {networkAddr} MASK {networkMask}";
                        Log($"Reintentando con máscara: route {args2}");
                        var result2 = ExecuteCommand("route", args2);
                        await Task.Delay(100);
                        if (!result2.success && !result2.error.Contains("not found") && !result2.error.Contains("no se encuentra"))
                        {
                            Log($"FALLO PERSISTENTE al eliminar ruta para {cidrNetwork}.");
                            failCount++;
                        }
                        else { Log("Éxito (o no encontrada) en el segundo intento."); successCount++; }
                    }
                }
                else { Log($"Advertencia: No se pudo parsear '{cidrNetwork}' para intentar eliminar ruta."); failCount++; }
                Log("-------------------------");
                await Task.Delay(200);
            }

            Log($"Proceso de limpieza forzada de rutas completado.");
            Log($"Intentos exitosos (o ruta no encontrada): {successCount}");
            Log($"Intentos fallidos (o no parseados): {failCount}");

            if (failCount > 0) { Log("\nAlgunas rutas no pudieron ser eliminadas automáticamente. Revisa la salida y usa 'route print' en cmd (admin)."); }

            Console.WriteLine("\nPresiona Enter para volver al menú principal..."); Console.ReadLine(); Console.Clear();
        }
        static void RegisterClosedHandler()
        {
            if (connection == null) return;
            connection.Closed -= OnConnectionClosedAsync;
            connection.Closed += OnConnectionClosedAsync;
            Log("Manejador para conexión cerrada registrado.");
        }

        private static async Task OnConnectionClosedAsync(Exception? error)
        {
            Log($"*** ALERTA: Conexión SignalR cerrada inesperadamente: {error?.Message} ***");
            await CleanupWinTunAndNetwork();
            Log("Túnel detenido debido a desconexión. Regresando al menú principal.");
            await Task.Delay(1500);
        }

        static async Task StartTunnelMode()
        {
            await Task.Delay(1000);
            Console.Clear();
            Log("\n--- Iniciando Modo Túnel ---");

            if (connection == null) { Log("Error crítico: Objeto de conexión SignalR es nulo."); await Task.Delay(1500); return; }
            if (wintunAdapter != IntPtr.Zero || networkWasConfiguredByApp)
            { Log("Advertencia: Estado previo detectado, limpiando antes de iniciar..."); await CleanupWinTunAndNetwork(); await Task.Delay(500); }

            bool tunnelSuccessfullyStarted = false;
            try
            {
                if (connection.State != HubConnectionState.Connected)
                {
                    Log("Conectando al servidor SignalR...");
                    try { await connection.StartAsync(); Log("¡Conectado!"); }
                    catch (Exception ex) { Log($"¡Fallo la conexión!: {ex.Message}"); await Task.Delay(1500); return; }
                }

                Log("Registrando alias de cliente...");
                await RegisterClientAlias();
                if (string.IsNullOrWhiteSpace(clientAlias))
                { Log("Error: No se pudo registrar el alias. Abortando modo túnel."); if (connection.State == HubConnectionState.Connected) await connection.StopAsync(); await Task.Delay(1500); return; }
                Log($"Alias '{clientAlias}' registrado.");

                Log("Inicializando adaptador WinTun...");
                if (!InitializeWinTun())
                { Log("Error: Fallo al inicializar WinTun. Abortando modo túnel."); if (connection.State == HubConnectionState.Connected) await connection.StopAsync(); await Task.Delay(1500); return; }
                Log("WinTun inicializado.");

                networkWasConfiguredByApp = false;
                if (autoConfigureNetwork)
                {
                    Log("Intentando configuración automática de red...");
                    if (string.IsNullOrWhiteSpace(configuredLocalIp) || networksToRoute.Count == 0)
                    { Log("Advertencia: Config. automática habilitada pero faltan IP local o redes a enrutar. Saltando."); }
                    else
                    {
                        Log("Obteniendo detalles de la interfaz WinTun...");
                        if (!GetWinTunInterfaceDetails() || !wintunInterfaceIndex.HasValue)
                        { Log("Error: No se pudieron obtener los detalles de la interfaz WinTun. Abortando."); await CleanupWinTunAndNetwork(); await Task.Delay(1500); return; }

                        Log($"Configurando IP {configuredLocalIp} en interfaz {wintunInterfaceIndex.Value}...");
                        string interfaceIdentifier = $"\"{wintunInterfaceIndex.Value}\"";

                        Log($"Configurando IP {configuredLocalIp} en interfaz con Identificador={interfaceIdentifier}...");

                        var ipResult = ExecuteCommand("netsh", $"interface ip set address {interfaceIdentifier} static {configuredLocalIp} {wintunSubnetMask}");
                        if (!ipResult.success)
                        {
                            Log("Error: Fallo al configurar la dirección IP. Abortando.");
                            await CleanupWinTunAndNetwork();
                            await Task.Delay(1500); return;
                        }
                        bool allRoutesAdded = true;
                        Log("Añadiendo rutas de red...");
                        foreach (var cidrNetwork in networksToRoute)
                        {
                            if (ParseCidr(cidrNetwork, out string netAddr, out string netMask))
                            {
                                string routeArgs = $"add {netAddr} MASK {netMask} {configuredLocalIp} IF {wintunInterfaceIndex.Value}";
                                var routeResult = ExecuteCommand("route", routeArgs);
                                if (!routeResult.success) { Log($"Error: Fallo al añadir ruta para {cidrNetwork}. Verifique la salida anterior."); allRoutesAdded = false; }
                            }
                            else { Log($"Advertencia: No se pudo parsear '{cidrNetwork}' para añadir ruta."); allRoutesAdded = false; }
                            await Task.Delay(100);
                        }

                        if (allRoutesAdded) { Log("Configuración de red aplicada con éxito."); networkWasConfiguredByApp = true; }
                        else { Log("Error: Fallaron algunas configuraciones de red. Limpiando y abortando..."); await CleanupWinTunAndNetwork(); await Task.Delay(1500); return; }
                    }
                }
                else { Log("Configuración automática de red deshabilitada. (Se requiere configuración manual)."); }

                Log("Iniciando procesamiento de paquetes WinTun...");
                if (wintunCancelTokenSource == null || wintunCancelTokenSource.IsCancellationRequested) { wintunCancelTokenSource = new CancellationTokenSource(); }
                Task readLoopTask = Task.Run(() => WinTunReadLoopAsync(wintunCancelTokenSource.Token), wintunCancelTokenSource.Token);

                tunnelSuccessfullyStarted = true;
                Log("\n--- Modo Túnel Iniciado Correctamente ---");
                Log("Comandos disponibles: 'send <archivo> <alias>', 'peer <alias>', 'status', 'exit'");

                await HandleUserInputTunnelMode();

                if (!readLoopTask.IsCompleted) { Log("Esperando finalización de tareas de lectura..."); await readLoopTask; }
            }
            catch (Exception ex) { Log($"\nError fatal durante el modo túnel: {ex.Message}\n{ex.StackTrace}"); }
            finally
            { Log("\nSaliendo del modo túnel..."); await CleanupWinTunAndNetwork(); Log("Limpieza post-túnel completada. Regresando al menú principal..."); await Task.Delay(1500); Console.Clear(); }
        }

        static async Task HandleUserInputTunnelMode()
        {
            Log($"--- Entrando al bucle de comandos del túnel para '{clientAlias}' ---");
            while (true)
            {
                if (connection?.State != HubConnectionState.Connected) { Log("\nError crítico: Conexión SignalR perdida. Saliendo del modo túnel..."); return; }
                if (wintunSession == IntPtr.Zero || wintunAdapter == IntPtr.Zero) { Log("\nError crítico: Sesión/Adaptador WinTun no válido. Saliendo del modo túnel..."); return; }

                Console.Write($"({clientAlias}) > ");
                string? input = Console.ReadLine()?.Trim();
                string[]? parts = input?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts == null || parts.Length == 0) continue;
                string command = parts[0].ToLowerInvariant();
                Log($"Comando introducido: {input}");

                switch (command)
                {
                    case "exit": Log("Solicitando salida del modo túnel..."); return;
                    case "send":
                        if (parts.Length >= 3)
                        {
                            string filePath = string.Join(" ", parts.Skip(1).Take(parts.Length - 2));
                            string recipientAliasSend = parts.Last();
                            if (!File.Exists(filePath)) { Log($"Error: Archivo '{filePath}' no encontrado."); }
                            else
                            {
                                if (recipientAliasSend.Equals(clientAlias, StringComparison.OrdinalIgnoreCase)) { Log("Advertencia: Intentando enviar archivo a ti mismo."); }
                                Log($"Preparando envío de '{filePath}' a '{recipientAliasSend}'...");
                                await SendFileAsync(filePath, recipientAliasSend);
                            }
                        }
                        else { Log("Uso: send <ruta_archivo> <alias_destino>"); }
                        break;
                    case "peer":
                        if (parts.Length == 2)
                        {
                            string potentialPeer = parts[1];
                            if (potentialPeer.Equals(clientAlias, StringComparison.OrdinalIgnoreCase)) { Log("Advertencia: No puedes establecerte a ti mismo como peer VPN."); }
                            else
                            {
                                peerAlias = potentialPeer;
                                Log($"Peer VPN establecido a: '{peerAlias}'.");
                                ipPacketSentCounter = 0;
                                currentSettings.DefaultPeerAlias = peerAlias; SaveConfiguration();
                            }
                        }
                        else { Log("Uso: peer <alias_destino_vpn>"); }
                        break;
                    case "status":
                        Log("\n--- Estado Actual del Túnel ---");
                        Log($" Estado SignalR  : {connection?.State}");
                        Log($" Alias Cliente   : {clientAlias}");
                        Log($" Peer VPN        : {peerAlias ?? "(No establecido)"}");
                        Log($" Config. Red     : {(networkWasConfiguredByApp ? "Aplicada por App" : "(Manual / No Aplicada)")}");
                        Log($" IP WinTun Local : {(networkWasConfiguredByApp ? configuredLocalIp : "(N/A)")}");
                        Log($" Idx/Nombre Iface: {wintunInterfaceIndex?.ToString() ?? "N/A"} / {wintunInterfaceName ?? "N/A"}");
                        Log($" WinTun Adapter : {(wintunAdapter != IntPtr.Zero ? "Activo" : "Inactivo")}");
                        Log($" WinTun Session : {(wintunSession != IntPtr.Zero ? "Activa" : "Inactiva")}");
                        Log("-------------------------------\n");
                        break;
                    default: if (!string.IsNullOrWhiteSpace(input)) { Log("Comando desconocido. Comandos: send, peer, status, exit."); } break;
                }
                await Task.Delay(50);
            }
        }

        static async Task RegisterClientAlias()
        {
            bool registrationAttempted = false;
            if (!string.IsNullOrWhiteSpace(clientAlias) && connection != null)
            {
                Log($"Intentando registrar alias preconfigurado: '{clientAlias}'...");
                registrationAttempted = true;
                try
                {
                    await connection.InvokeAsync("RegisterAlias", new RegisterAliasRequest { Alias = clientAlias });
                    await Task.Delay(1000);
                    if (!string.IsNullOrWhiteSpace(clientAlias)) { Log($"Alias '{clientAlias}' preconfigurado registrado con éxito (o ya estaba registrado)."); return; }
                    else { Log("Fallo al registrar alias preconfigurado. Se solicitará uno nuevo."); }
                }
                catch (Exception ex) { Log($"Error al intentar registrar alias preconfigurado '{clientAlias}': {ex.Message}"); clientAlias = null; }
            }

            while (string.IsNullOrWhiteSpace(clientAlias) && connection?.State == HubConnectionState.Connected)
            {
                Console.Write("Ingresa el alias que deseas usar: ");
                string? inputAlias = Console.ReadLine()?.Trim();
                Log($"Intento de alias manual: {inputAlias ?? "NULL"}");

                if (!string.IsNullOrWhiteSpace(inputAlias))
                {
                    try
                    {
                        clientAlias = inputAlias;
                        Log($"Intentando registrar alias: '{clientAlias}'...");
                        await connection.InvokeAsync("RegisterAlias", new RegisterAliasRequest { Alias = clientAlias });
                        await Task.Delay(1000);
                        if (!string.IsNullOrWhiteSpace(clientAlias))
                        {
                            Log($"Alias '{clientAlias}' registrado con éxito.");
                            currentSettings.ClientAlias = clientAlias; SaveConfiguration(); break;
                        }
                        else { Log("Fallo al registrar el alias introducido."); }
                    }
                    catch (Exception ex) { Log($"Error al llamar al método RegisterAlias: {ex.Message}"); clientAlias = null; }
                }
                else { Log("Alias inválido."); }
                if (connection?.State != HubConnectionState.Connected) { Log("Error: Conexión perdida durante el registro de alias."); break; }
            }
            if (string.IsNullOrWhiteSpace(clientAlias)) { Log("Advertencia: No se pudo establecer un alias de cliente."); }
        }

        static void RegisterServerHandlers()
        {
            if (connection == null) { Log("Error: Intento de registrar handlers sin conexión."); return; }

            connection.On<DataBatchContainer>("ReceiveDataBatch", async (container) =>
            {
                if (container?.Batches == null || container.Batches.Count == 0) { Log("Error: Recibido contenedor de lotes nulo o vacío. Ignorando."); return; }
                int ipPacketCount = 0; long ipBytesCount = 0; int fileFragCount = 0;

                foreach (var batch in container.Batches)
                {
                    if (batch.Data == null || batch.Data.Length == 0) { Log($"Error: Recibido lote nulo o vacío para TransferId {batch.TransferId}. Ignorando."); continue; }
                    switch (batch.Type)
                    {
                        case MessageType.FileFragment: fileFragCount++; await HandleFileFragmentAsync(batch); break;
                        case MessageType.IpPacket:
                            if (wintunSession == IntPtr.Zero) { if (ipPacketCount == 0) Log("\nAdvertencia: Recibido paquete IP pero la sesión WinTun no está activa. Descartando."); ipPacketCount++; continue; }
                            byte[] packetData = batch.Data; IntPtr sendPacketPtr = IntPtr.Zero;
                            try
                            {
                                sendPacketPtr = WinTun.WintunAllocateSendPacket(wintunSession, (uint)packetData.Length);
                                if (sendPacketPtr != IntPtr.Zero) { Marshal.Copy(packetData, 0, sendPacketPtr, packetData.Length); WinTun.WintunSendPacket(wintunSession, sendPacketPtr); ipPacketCount++; ipBytesCount += packetData.Length; }
                                else { LogInline($"\rAdvertencia: No se pudo alocar buffer de envío WinTun (posiblemente lleno). Paquete descartado."); }
                            }
                            catch (Exception ex) { Log($"\nError al inyectar paquete IP en WinTun: {ex.Message}"); }
                            break;
                        default: Log($"\rRecibido lote de tipo desconocido ({batch.Type}) para TransferId {batch.TransferId}. Ignorando."); break;
                    }
                }
                if (ipPacketCount > 0) { LogInline($"\rPaquetes IP recibidos por SignalR e inyectados en WinTun: {ipPacketCount} ({ipBytesCount} bytes)"); }

            });

            connection.On<string>("AliasRegistered", (alias) => { Log($"Respuesta Servidor: ¡Alias '{alias}' registrado con éxito!"); });
            connection.On<string>("AliasRegistrationFailed", (error) => { Log($"Respuesta Servidor: Error al registrar alias: {error}"); clientAlias = null; });
            connection.On<string>("SendMessageFailed", (error) => { Log($"Respuesta Servidor: Error al enviar mensaje: {error}"); });
            Log("Manejadores de eventos SignalR registrados.");
        }

        private static async Task HandleFileFragmentAsync(DataBatch batch)
        {
            ReassemblyState? state = null;
            if (batch.IsFirst)
            {
                if (_activeTransfers.ContainsKey(batch.TransferId)) { Log($"\rAdvertencia: Recibido primer fragmento para TransferId {batch.TransferId} que ya estaba activo. Reiniciando estado."); _activeTransfers.TryRemove(batch.TransferId, out _); }
                state = new ReassemblyState(batch.TransferId, batch.Type) { Filename = batch.Filename, OriginalSize = batch.OriginalSize };
                if (!_activeTransfers.TryAdd(batch.TransferId, state)) { Log($"\rError: No se pudo añadir el estado para TransferId {batch.TransferId}."); return; }
                Log($"\rIniciando reensamblaje para TransferId {batch.TransferId}, Archivo: '{state.Filename}', Tamaño: {state.OriginalSize}");
            }
            else { if (!_activeTransfers.TryGetValue(batch.TransferId, out state)) { Log($"\rAdvertencia: Recibido fragmento ({batch.Sequence}) para TransferId {batch.TransferId} que no está activo. Ignorando."); return; } }

            if (state != null)
            {
                if (state.AddFragment(batch))
                {
                    if (state.OriginalSize.HasValue && state.OriginalSize.Value > 0) { double progress = (double)state.Buffer.Count / state.OriginalSize.Value * 100; LogInline($"\rRecibiendo {state.Filename} ({state.TransferId.ToString("N")[..4]}...): {progress:F2}% ({state.Buffer.Count}/{state.OriginalSize.Value} bytes)"); }
                    else { LogInline($"\rRecibiendo {state.Filename} ({state.TransferId.ToString("N")[..4]}...): {state.Buffer.Count} bytes recibidos."); }

                    if (batch.IsLast)
                    {
                        Console.WriteLine();
                        string? savedPath = await state.SaveFileAsync();
                        if (savedPath != null) { Log($"¡Transferencia {batch.TransferId} completada! Archivo guardado como: {savedPath}"); if (state.OriginalSize.HasValue && state.Buffer.Count != state.OriginalSize.Value) { Log($"Advertencia: Tamaño reensamblado ({state.Buffer.Count}) no coincide con original ({state.OriginalSize.Value}) para {batch.TransferId}."); } }
                        else { Log($"Error al completar la transferencia {batch.TransferId}. Falló al guardar el archivo."); }
                        if (!_activeTransfers.TryRemove(batch.TransferId, out _)) { Log($"Advertencia: No se pudo eliminar el estado para TransferId {batch.TransferId} del diccionario."); }
                        Log($"Estado de TransferId {batch.TransferId} eliminado.");
                    }
                }
                else
                {
                    Log($"\rAdvertencia: Recibido fragmento {batch.Sequence} fuera de orden (esperado {state.ExpectedSequence}) para TransferId {batch.TransferId}. Cancelando reensamblaje.");
                    if (_activeTransfers.TryRemove(batch.TransferId, out _)) { Log($"Estado de TransferId {batch.TransferId} eliminado debido a fragmento fuera de orden."); }
                    else { Log($"Advertencia: Estado para TransferId {batch.TransferId} ya no estaba en el diccionario al detectar fragmento fuera de orden."); }
                }
            }
        }

        static bool InitializeWinTun()
        {
            Log("Inicializando adaptador WinTun...");
            string adapterName = $"SGR_{clientAlias ?? "Default"}";
            try
            {
                wintunAdapter = WinTun.WintunCreateAdapter(adapterName, "Tunnel", null);
            }
            catch (DllNotFoundException) { Log("Error FATAL: wintun.dll no encontrado. Asegúrate de que está junto al .exe y es la versión correcta (x64/x86)."); return false; }
            catch (Exception ex) { Log($"Error inesperado al llamar a WintunCreateAdapter: {ex.Message}"); return false; }

            if (wintunAdapter == IntPtr.Zero) { Log("Error al crear el adaptador WinTun. Código de error: " + Marshal.GetLastWin32Error() + ". ¿Permisos de Admin? ¿DLL correcta?"); return false; }
            Log($"Adaptador WinTun '{adapterName}' creado (Handle: {wintunAdapter}).");

            uint capacity = 4 * 1024 * 1024;
            try
            {
                wintunSession = WinTun.WintunStartSession(wintunAdapter, capacity);
            }
            catch (Exception ex) { Log($"Error inesperado al llamar a WintunStartSession: {ex.Message}"); WinTun.WintunCloseAdapter(wintunAdapter); wintunAdapter = IntPtr.Zero; return false; }

            if (wintunSession == IntPtr.Zero) { Log("Error al iniciar la sesión WinTun. Código de error: " + Marshal.GetLastWin32Error()); WinTun.WintunCloseAdapter(wintunAdapter); wintunAdapter = IntPtr.Zero; return false; }
            Log($"Sesión WinTun iniciada con éxito (Handle: {wintunSession}). Capacidad: {capacity / 1024 / 1024}MB.");
            return true;
        }

        static bool GetWinTunInterfaceDetails()
        {
            wintunInterfaceIndex = null; wintunInterfaceName = null;
            string expectedAdapterName = $"SGR_{clientAlias ?? "Default"}";
            Log($"Buscando detalles de interfaz para: '{expectedAdapterName}'...");
            var netshResult = ExecuteCommand("netsh", "interface ipv4 show interfaces");

            if (!netshResult.success) { Log($"Error: No se pudo ejecutar 'netsh'. Error: {netshResult.error}"); return false; }
            if (string.IsNullOrWhiteSpace(netshResult.output)) { Log("Error: 'netsh' no devolvió ninguna salida."); return false; }

            Log($"--- Salida Completa de 'netsh interface ipv4 show interfaces' ---\n{netshResult.output}\n--------------------------------------------------------------");

            string[] lines = netshResult.output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries); bool found = false;
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.Length == 0 || trimmedLine.StartsWith("---") || trimmedLine.StartsWith("Idx", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = trimmedLine.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 5)
                {
                    string potentialName = string.Join(" ", parts.Skip(4)).Trim();

                    if (potentialName.Contains(expectedAdapterName, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"Línea candidata encontrada: Idx='{parts[0]}', Name='{potentialName}'");
                        if (int.TryParse(parts[0].Trim(), out int index))
                        {
                            wintunInterfaceIndex = index;
                            wintunInterfaceName = potentialName;
                            Log($"Interfaz WinTun encontrada y parseada: Idx={wintunInterfaceIndex}, Name='{wintunInterfaceName}'");
                            found = true; break;
                        }
                        else { Log($"Advertencia: Se encontró la interfaz '{potentialName}' pero no se pudo parsear el índice '{parts[0]}'."); }
                    }
                }
            }
            if (!found) { Log($"Error: No se encontró una interfaz activa que contenga el nombre '{expectedAdapterName}' en la salida de netsh."); return false; }
            return true;
        }

        static (bool success, string output, string error) ExecuteCommand(string command, string arguments)
        {
            Log($"Ejecutando: {command} {arguments}");
            string output = string.Empty; string error = string.Empty;
            ProcessStartInfo procStartInfo = new ProcessStartInfo(command, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, Verb = "runas" };
            using (Process process = new Process { StartInfo = procStartInfo })
            {
                try
                {
                    process.Start(); output = process.StandardOutput.ReadToEnd(); error = process.StandardError.ReadToEnd(); process.WaitForExit(10000);
                    if (!process.HasExited) { process.Kill(true); Log("Error: El comando tardó demasiado en responder y fue terminado."); return (false, output, "Timeout"); }
                    if (process.ExitCode == 0) { Log("Comando ejecutado con éxito."); return (true, output, error); }
                    else { Log($"Error al ejecutar comando (Código: {process.ExitCode})"); if (!string.IsNullOrWhiteSpace(error)) Log($"Error Output: {error}"); if (!string.IsNullOrWhiteSpace(output)) Log($"Standard Output (puede contener error): {output}"); return (false, output, error); }
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { Log("Error: La operación fue cancelada (posiblemente UAC denegado). Se requieren permisos de administrador."); return (false, output, "UAC Denied / Cancelled"); }
                catch (Exception ex) { Log($"Excepción al ejecutar comando '{command} {arguments}': {ex.Message}"); return (false, output, ex.Message); }
            }
        }

        static bool ParseCidr(string cidr, out string network, out string mask)
        {
            network = string.Empty; mask = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(cidr) || !cidr.Contains('/')) return false;
                var parts = cidr.Split('/'); if (parts.Length != 2) return false;
                if (!IPAddress.TryParse(parts[0], out _)) return false; network = parts[0];
                if (!int.TryParse(parts[1], out int prefixInt) || prefixInt < 0 || prefixInt > 32) return false; string prefix = parts[1];
                switch (prefix) { case "32": mask = "255.255.255.255"; break; case "24": mask = "255.255.255.0"; break; case "16": mask = "255.255.0.0"; break; case "8": mask = "255.0.0.0"; break; case "0": mask = "0.0.0.0"; break; default: uint maskUInt = prefixInt == 0 ? 0 : (0xFFFFFFFF << (32 - prefixInt)); mask = new IPAddress(IPAddress.HostToNetworkOrder((int)maskUInt)).ToString(); break; }
                return true;
            }
            catch { return false; }
        }

        static async Task WinTunReadLoopAsync(CancellationToken cancellationToken)
        {
            Log("Iniciando bucle de lectura de WinTun..."); long packetCounter = 0;
            try
            {
                if (wintunSession == IntPtr.Zero) { Log("Error: Intento de iniciar bucle de lectura sin sesión WinTun válida."); return; }
                while (!cancellationToken.IsCancellationRequested)
                {
                    IntPtr packetPtr = IntPtr.Zero;
                    try
                    {
                        uint packetSize; packetPtr = WinTun.WintunReceivePacket(wintunSession, out packetSize);
                        if (packetPtr == IntPtr.Zero) { if (!cancellationToken.IsCancellationRequested) { Log($"\nWintunReceivePacket retornó NULL inesperadamente después de {packetCounter} paquetes. La sesión puede haberse cerrado."); } break; }
                        if (packetSize > 0) { packetCounter++; byte[] packetData = new byte[packetSize]; Marshal.Copy(packetPtr, packetData, 0, (int)packetSize); await SendIpPacketViaSignalRAsync(packetData); }
                    }
                    catch (ObjectDisposedException) { Log("\nLa sesión de WinTun fue cerrada mientras se esperaba un paquete."); break; }
                    catch (Exception ex) { Log($"\nError dentro del bucle de lectura WinTun: {ex.Message}"); await Task.Delay(200, cancellationToken); }
                    finally { if (packetPtr != IntPtr.Zero) { WinTun.WintunReleaseReceivePacket(wintunSession, packetPtr); } }
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException) { Log("\nBucle de lectura de WinTun cancelado limpiamente."); }
            catch (Exception ex) { Log($"\nError fatal en WinTunReadLoopAsync: {ex.Message}\n{ex.StackTrace}"); }
            finally { Log($"\nBucle de lectura de WinTun finalizado. Total paquetes leídos: {packetCounter}."); }
        }

        static async Task SendIpPacketViaSignalRAsync(byte[] packetData)
        {
            if (connection?.State != HubConnectionState.Connected) { return; }
            if (string.IsNullOrEmpty(peerAlias)) { if (ipPacketSentCounter == 0) Log("\nAdvertencia: Peer VPN no establecido ('peer <alias>'). Paquetes IP salientes descartados."); ipPacketSentCounter++; return; }
            if (packetData == null || packetData.Length == 0) { Log("Advertencia: Intento de enviar paquete IP vacío."); return; }

            try
            {
                var batch = new DataBatch { Type = MessageType.IpPacket, TransferId = IpTransferId, Sequence = (int)(ipPacketSentCounter++ % int.MaxValue), IsFirst = false, IsLast = false, Data = packetData };
                var batchesToSend = new List<DataBatch> { batch };
                var sendRequest = new SendDataRequest { RecipientAlias = peerAlias, BatchContainer = new DataBatchContainer { Batches = batchesToSend } };
                _ = connection.InvokeAsync("SendData", sendRequest);

            }
            catch (Exception ex) { Log($"\nError al enviar paquete IP vía SignalR: {ex.Message}"); }
        }

        static async Task SendFileAsync(string filePath, string recipientAlias)
        {
            if (connection == null || connection.State != HubConnectionState.Connected || string.IsNullOrEmpty(clientAlias) || string.IsNullOrEmpty(recipientAlias)) { Log("Error: No se puede enviar archivo. Conexión/Alias inválido."); return; }
            const int fragmentSize = 1024 * 8;
            const int batchSize = 50;
            string filename = Path.GetFileName(filePath);
            try
            {
                byte[] fileBytes = await File.ReadAllBytesAsync(filePath); long totalBytes = fileBytes.LongLength; int sequence = 0; long offset = 0; Guid transferId = Guid.NewGuid();
                Log($"Preparando envío de archivo '{filePath}' (TransferId: {transferId}) a '{recipientAlias}' ({totalBytes} bytes).");
                List<DataBatch> currentBatchList = new List<DataBatch>();
                Stopwatch sw = Stopwatch.StartNew();

                while (offset < totalBytes)
                {
                    int bytesToSend = (int)Math.Min(fragmentSize, totalBytes - offset); byte[] fragmentData = new byte[bytesToSend];
                    Array.Copy(fileBytes, offset, fragmentData, 0, bytesToSend);
                    bool isFirstFileFragment = (offset == 0); bool isLastFileFragment = (offset + bytesToSend >= totalBytes);
                    var batch = new DataBatch { Type = MessageType.FileFragment, TransferId = transferId, Sequence = sequence, IsFirst = isFirstFileFragment, IsLast = isLastFileFragment, Data = fragmentData, Filename = isFirstFileFragment ? filename : null, OriginalSize = isFirstFileFragment ? (long?)totalBytes : null };
                    currentBatchList.Add(batch); offset += bytesToSend; sequence++;

                    if (currentBatchList.Count >= batchSize || isLastFileFragment)
                    {
                        var sendRequest = new SendDataRequest { RecipientAlias = recipientAlias, BatchContainer = new DataBatchContainer { Batches = currentBatchList } };
                        await connection.InvokeAsync("SendData", sendRequest);
                        double progress = (double)offset / totalBytes * 100;
                        LogInline($"\rEnviando {filename} ({transferId.ToString("N")[..4]}...): {progress:F2}% ({offset}/{totalBytes} bytes)");
                        currentBatchList = new List<DataBatch>();
                        await Task.Delay(1);
                    }
                }
                sw.Stop();
                Console.WriteLine();
                Log($"Envío del archivo '{filePath}' (TransferId: {transferId}) completado. {sequence} fragmentos enviados en {sw.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex) { Log($"\nError durante el envío del archivo: {ex.Message}\n{ex.StackTrace}"); }
        }

        static void CleanupWinTunResources()
        {
            Log("Limpiando recursos de WinTun...");
            try { if (wintunCancelTokenSource != null && !wintunCancelTokenSource.IsCancellationRequested) { wintunCancelTokenSource.Cancel(); Log("Token de cancelación solicitado."); Task.Delay(50).Wait(); } }
            catch (ObjectDisposedException) { Log("Advertencia: Token source ya dispuesto al intentar cancelar."); }

            if (wintunSession != IntPtr.Zero) { try { WinTun.WintunEndSession(wintunSession); Log("Sesión WinTun finalizada."); } catch (Exception ex) { Log($"Excepción al finalizar sesión WinTun: {ex.Message}"); } finally { wintunSession = IntPtr.Zero; } }
            if (wintunAdapter != IntPtr.Zero) { try { WinTun.WintunCloseAdapter(wintunAdapter); Log("Adaptador WinTun eliminado."); } catch (Exception ex) { Log($"Excepción al eliminar adaptador WinTun: {ex.Message}"); } finally { wintunAdapter = IntPtr.Zero; } }
            try { wintunCancelTokenSource?.Dispose(); } catch (ObjectDisposedException) { }
            wintunCancelTokenSource = new CancellationTokenSource();
            Log("Recursos WinTun limpiados.");
        }

        static async Task CleanupWinTunAndNetwork()
        {
            Log("Iniciando limpieza completa (Red y WinTun)...");
            bool neededNetworkCleanup = networkWasConfiguredByApp;

            if (neededNetworkCleanup)
            {
                Log("Intentando revertir configuración de red aplicada..."); bool routesDeletedOk = true;
                if (!wintunInterfaceIndex.HasValue && wintunAdapter != IntPtr.Zero) { Log("Intentando re-obtener detalles de interfaz para limpieza..."); GetWinTunInterfaceDetails(); }
                var routesToRemove = new List<string>(networksToRoute); networksToRoute.Clear();
                foreach (var cidrNetwork in routesToRemove)
                {
                    if (ParseCidr(cidrNetwork, out string networkAddr, out string networkMask))
                    {
                        string args1 = $"delete {networkAddr}"; Log($"Ejecutando: route {args1}"); var result1 = ExecuteCommand("route", args1); await Task.Delay(100);
                        if (!result1.success && !result1.error.Contains("not found") && !result1.error.Contains("no se encuentra")) { string args2 = $"delete {networkAddr} MASK {networkMask}"; Log($"Reintentando con máscara: route {args2}"); var result2 = ExecuteCommand("route", args2); await Task.Delay(100); if (!result2.success && !result2.error.Contains("not found") && !result2.error.Contains("no se encuentra")) { Log($"FALLO PERSISTENTE al eliminar ruta para {cidrNetwork}."); routesDeletedOk = false; } else { Log("Éxito (o no encontrada) en el segundo intento."); } } else if (result1.success) { Log("Éxito (o no encontrada)."); }
                    }
                    else { Log($"Advertencia: No se pudo parsear '{cidrNetwork}' para eliminar ruta."); }
                    await Task.Delay(200);
                }
                if (!string.IsNullOrWhiteSpace(configuredLocalIp) && (wintunInterfaceIndex.HasValue || !string.IsNullOrWhiteSpace(wintunInterfaceName)))
                {
                    string interfaceIdentifier = !string.IsNullOrWhiteSpace(wintunInterfaceName) ? $"name=\"{wintunInterfaceName}\"" : $"\"{wintunInterfaceIndex.Value}\"";
                    Log($"Intentando resetear IP para interfaz {interfaceIdentifier} a DHCP..."); var setResult = ExecuteCommand("netsh", $"interface ip set address {interfaceIdentifier} source=dhcp");
                    if (!setResult.success && !(setResult.error.Contains("file was not found") || setResult.error.Contains("no se encontró el archivo") || setResult.error.Contains("element not found"))) { Log($"Fallo al resetear IP a DHCP para {interfaceIdentifier}. Detalle: {setResult.error}"); }
                    else if (setResult.success) { Log($"IP reseteada a DHCP para {interfaceIdentifier}."); }
                }
                if (routesDeletedOk) { Log("Limpieza de rutas de red completada."); }
                networkWasConfiguredByApp = false;
            }
            else { Log("Saltando limpieza de red (no fue configurada por la app o ya limpiada)."); }

            configuredLocalIp = null; wintunInterfaceIndex = null; wintunInterfaceName = null; if (!neededNetworkCleanup) networksToRoute.Clear();
            CleanupWinTunResources();
            Log("Limpieza completa finalizada.");
        }

    }
}