# ClienteSGR - Cliente de Túnel de Red sobre SignalR con WinTun

ClienteSGR es una aplicación de consola .NET diseñada para crear un túnel de red virtual entre dos clientes a través de un servidor SignalR. Utiliza WinTun para la creación de interfaces de red virtuales y la manipulación de paquetes IP, permitiendo así emular una conexión VPN punto a punto. Además de la tunelización de paquetes IP, ClienteSGR también soporta la transferencia de archivos entre los clientes conectados.

Este proyecto es ideal para escenarios donde se necesita una comunicación de red directa entre dos máquinas que podrían estar detrás de NATs o firewalls, utilizando un servidor SignalR como intermediario para el transporte de datos.

## Características Principales

* **Tunelización IP:** Crea un túnel virtual para el tráfico IP entre dos peers utilizando WinTun.
* **Transferencia de Archivos:** Permite enviar y recibir archivos de forma fragmentada entre los peers.
* **Comunicación vía SignalR:** Utiliza ASP.NET Core SignalR para la comunicación en tiempo real con un servidor central y entre peers.
    * Usa **MessagePack** para una serialización binaria eficiente de los mensajes.
* **Configuración Automática de Red (Opcional):**
    * Puede configurar automáticamente la dirección IP y las rutas para la interfaz WinTun creada.
    * Requiere permisos de administrador.
* **Configuración Persistente:** Guarda y carga la configuración desde un archivo `Config.json` (URL del servidor, alias, configuración de red, etc.).
* **Interfaz de Línea de Comandos (CLI):**
    * Menú principal para iniciar el túnel, configurar la aplicación o reparar la red.
    * Comandos interactivos durante el modo túnel para enviar archivos, cambiar el peer VPN y ver el estado.
* **Logging Detallado:** Registra eventos y errores en la consola y en un archivo de log para facilitar la depuración.
* **Manejo de Recursos:** Limpia adecuadamente los recursos de WinTun y la configuración de red al finalizar.

## Tecnologías Utilizadas

* **.NET 9** (Puede ser adaptable a otras versiones de .NET Core/.NET 5+ con ajustes mínimos)
* **ASP.NET Core SignalR Client**
* **MessagePack** (para la serialización en SignalR)
* **WinTun** (requiere `wintun.dll`)

## Requisitos Previos

* **Windows:** Actualmente, debido al uso de WinTun, el cliente está limitado a sistemas operativos Windows.
* **.NET Runtime:** El runtime de .NET correspondiente a la versión de compilación (ej. .NET 9).
* **`wintun.dll`:** Esta librería debe estar presente en el mismo directorio que el ejecutable `ClienteSGR.exe`. Puedes obtenerla del [sitio oficial de WireGuard (WinTun)](https://www.wintun.net/). Asegúrate de usar la versión (x86/amd64) que corresponda a la compilación de ClienteSGR.
* **Servidor SignalR (SGR):** Se necesita un servidor SignalR compatible (no incluido en este repositorio) que implemente los métodos de Hub esperados (`RegisterAlias`, `SendData`) y los métodos cliente (`ReceiveDataBatch`, etc.).
* **Permisos de Administrador:** Necesarios para crear el adaptador WinTun y para la configuración automática de la red (asignar IP, añadir rutas).

## Cómo Empezar

1.  **Clonar el Repositorio:**
    ```bash
    git clone [https://github.com/felipe55gonzalez/ClienteSGR.git](https://github.com/tu-usuario/ClienteSGR.git)
    cd ClienteSGR
    ```

2.  **Descargar `wintun.dll`:**
    * Ve a [www.wintun.net](https://www.wintun.net/) y descarga el archivo `wintun.dll`.
    * Copia `wintun.dll` al directorio de salida de la compilación (ej. `ClienteSGR/bin/Debug/net9.0/`).

3.  **Configurar la Aplicación:**
    * Edita el archivo `Config.json` que se genera en el directorio del ejecutable (o créalo si no existe) con los siguientes parámetros:
        ```json
        {
            "ServerUrl": "http://tu-servidor-signalr:puerto/datahub", // URL de tu Hub SignalR
            "AutoConfigureNetwork": true,                            // true para configurar IP/rutas, false para manual
            "ConfiguredLocalIp": "10.99.0.2",                        // IP para este cliente en la red WinTun (si AutoConfigureNetwork es true)
            "NetworksToRoute": [
                "10.99.0.1/32"                                     // Redes/IPs del peer a enrutar a través del túnel (formato CIDR)
            ],
            "ClientAlias": "ClienteA",                               // Alias único para este cliente
            "DefaultPeerAlias": "ClienteB"                           // Alias del peer al que se enviarán los paquetes IP por defecto
        }
        ```
    * La máscara de subred para `ConfiguredLocalIp` se asume como `255.255.255.0` por defecto en la configuración de red, pero las rutas pueden usar cualquier máscara CIDR.

4.  **Compilar y Ejecutar:**
    * Abre el proyecto con Visual Studio o usa la CLI de .NET:
        ```bash
        dotnet build
        cd ClienteSGR/bin/Debug/net9.0/  # O la ruta de salida correspondiente
        ./ClienteSGR.exe                 # O simplemente ClienteSGR.exe
        ```
    * **¡Importante!** Ejecuta `ClienteSGR.exe` como **Administrador** si `AutoConfigureNetwork` está habilitado o si es la primera vez que se crea el adaptador WinTun.

5.  **Uso:**
    * La aplicación presentará un menú:
        1.  **Iniciar Túnel:** Intenta conectar al servidor, registrar el alias, crear la interfaz WinTun y (opcionalmente) configurar la red.
        2.  **Configurar Aplicación:** Permite modificar los parámetros de `Config.json` interactivamente.
        3.  **Reparar Red (Limpieza Forzada):** Intenta eliminar las rutas configuradas por la app (útil si se cerró inesperadamente).
        4.  **Salir.**
    * Una vez en **Modo Túnel**:
        * `send <ruta_del_archivo> <alias_destino>`: Envía un archivo.
        * `peer <alias_destino_vpn>`: Establece el alias del peer para la tunelización IP.
        * `status`: Muestra el estado actual.
        * `exit`: Sale del modo túnel y limpia los recursos.

## Modelos de Datos (Simplificado)

La comunicación se basa en el envío de lotes de datos (`DataBatchContainer`) que contienen uno o más `DataBatch`.

* **`DataBatch`**:
    * `Type`: `FileFragment` o `IpPacket`.
    * `TransferId`: GUID para identificar una transferencia completa (útil para archivos).
    * `Sequence`: Número de secuencia del fragmento.
    * `IsFirst`, `IsLast`: Booleanos para marcar el primer y último fragmento de una transferencia.
    * `Data`: Los bytes del fragmento o paquete IP.
    * `Filename` (opcional): Nombre del archivo (en el primer fragmento).
    * `OriginalSize` (opcional): Tamaño total del archivo (en el primer fragmento).
    * Campos para metadatos IP (SourceIp, DestinationIp, ProtocolType) cuando `Type` es `IpPacket`.

## Desarrollo (Servidor SGR Requerido)

Este proyecto solo contiene el cliente. Para un funcionamiento completo, necesitarás un **Servidor SignalR** que implemente un Hub con la lógica para:

* Gestionar el registro de alias de clientes.
* Redirigir los `SendDataRequest` (que contienen `DataBatchContainer`) al alias destinatario correcto.
* Manejar las conexiones y desconexiones de los clientes.

**Ejemplo de Métodos del Hub (a implementar en el servidor):**

* `Task RegisterAlias(RegisterAliasRequest request)`
* `Task SendData(SendDataRequest request)`

**Ejemplo de Métodos del Cliente (invocados por el servidor):**

* `ReceiveDataBatch(DataBatchContainer container)`
* `AliasRegistered(string alias)`
* `AliasRegistrationFailed(string error)`
* `SendMessageFailed(string error)`

## Contribuciones

¡Las contribuciones son bienvenidas! Si tienes ideas para mejorar ClienteSGR, encuentras algún error o quieres añadir nuevas funcionalidades, por favor:

1.  Haz un Fork del proyecto.
2.  Crea una nueva rama (`git checkout -b feature/AmazingFeature`).
3.  Realiza tus cambios y haz commit (`git commit -m 'Add some AmazingFeature'`).
4.  Empuja tus cambios a la rama (`git push origin feature/AmazingFeature`).
5.  Abre un Pull Request.

## Licencia

Este proyecto se distribuye bajo la Licencia MIT. Ver el archivo `LICENSE` para más detalles.
