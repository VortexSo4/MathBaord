using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace MathBoard.Rendering;

public sealed unsafe class VulkanContext : IDisposable
{
    private readonly IWindow _window;

    // Backing fields
    private Vk _vk = null!;
    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;

    private Queue _graphicsQueue;
    private Queue _presentQueue;

    private KhrSurface _khrSurface = null!;
    private KhrSwapchain _khrSwapchain = null!;

    private QueueFamilyIndices _queueFamilies;

    // Public properties
    public Vk Vk => _vk;
    public Instance Instance => _instance;
    public SurfaceKHR Surface => _surface;
    public PhysicalDevice PhysicalDevice => _physicalDevice;
    public Device Device => _device;

    public Queue GraphicsQueue => _graphicsQueue;
    public Queue PresentQueue => _presentQueue;

    public KhrSurface KhrSurface => _khrSurface;
    public KhrSwapchain KhrSwapchain => _khrSwapchain;

    public QueueFamilyIndices QueueFamilies => _queueFamilies;
    
    public IWindow Window => _window;

    public VulkanContext(IWindow window)
    {
        _window = window;
    }

    public void Initialize()
    {
        _vk = Vk.GetApi();

        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        FindQueueFamilies();
        CreateLogicalDevice();

        // Подготовка к swapchain
        var support = QuerySwapchainSupport();
        Console.WriteLine($"Swapchain formats: {support.Formats.Length}");
        Console.WriteLine($"Swapchain present modes: {support.PresentModes.Length}");

        Console.WriteLine("VulkanContext initialized successfully");
    }

    private void CreateInstance()
    {
        var appNamePtr = (byte*)SilkMarshal.StringToPtr("MathBoard");

        try
        {
            ApplicationInfo appInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appNamePtr,
                PEngineName = appNamePtr,
                ApiVersion = Vk.Version12
            };

            var extensions = _window.VkSurface!.GetRequiredExtensions(out var extCount);

            InstanceCreateInfo createInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &appInfo,
                EnabledExtensionCount = extCount,
                PpEnabledExtensionNames = extensions
            };

            var result = _vk.CreateInstance(ref createInfo, null, out _instance);

            if (result != Result.Success)
                throw new Exception($"CreateInstance failed: {result}");

            Console.WriteLine("Instance created");
        }
        finally
        {
            SilkMarshal.Free((nint)appNamePtr);
        }
    }

    private void CreateSurface()
    {
        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new NotSupportedException("KHR_surface extension is not available.");

        var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null);
        _surface = surfaceHandle.ToSurface();

        Console.WriteLine($"Surface created (0x{_surface.Handle:X})");
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, null);

        if (deviceCount == 0)
            throw new Exception("No Vulkan-compatible GPU found.");

        var devices = stackalloc PhysicalDevice[(int)deviceCount];
        _vk.EnumeratePhysicalDevices(_instance, &deviceCount, devices);

        _physicalDevice = devices[0];

        PhysicalDeviceProperties props;
        _vk.GetPhysicalDeviceProperties(_physicalDevice, &props);

        var gpuName = SilkMarshal.PtrToString((nint)props.DeviceName);
        Console.WriteLine($"Selected GPU: {gpuName}");
    }

    private void FindQueueFamilies()
    {
        uint queueFamilyCount = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, null);

        var queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        _vk.GetPhysicalDeviceQueueFamilyProperties(_physicalDevice, &queueFamilyCount, queueFamilies);

        uint? graphicsFamily = null;
        uint? presentFamily = null;

        for (uint i = 0; i < queueFamilyCount; i++)
        {
            if ((queueFamilies[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                graphicsFamily = i;

            Bool32 presentSupport = false;
            _khrSurface.GetPhysicalDeviceSurfaceSupport(_physicalDevice, i, _surface, &presentSupport);

            if (presentSupport)
                presentFamily = i;

            if (graphicsFamily.HasValue && presentFamily.HasValue)
                break;
        }

        if (!graphicsFamily.HasValue)
            throw new Exception("Graphics queue family not found.");

        if (!presentFamily.HasValue)
            throw new Exception("Present queue family not found.");

        _queueFamilies = new QueueFamilyIndices(graphicsFamily.Value, presentFamily.Value);

        Console.WriteLine($"Graphics Queue Family: {_queueFamilies.GraphicsFamily}");
        Console.WriteLine($"Present  Queue Family: {_queueFamilies.PresentFamily}");
    }

    private void CreateLogicalDevice()
    {
        const float queuePriority = 1.0f;
        var queuePriorities = stackalloc float[1] { queuePriority };

        // Создаём два отдельных запроса на queue, даже если индексы совпадают
        var queueCreateInfos = stackalloc DeviceQueueCreateInfo[2];
        uint queueCreateInfoCount = 1;

        queueCreateInfos[0] = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamilies.GraphicsFamily,
            QueueCount = 1,
            PQueuePriorities = queuePriorities
        };

        if (_queueFamilies.GraphicsFamily != _queueFamilies.PresentFamily)
        {
            queueCreateInfos[1] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilies.PresentFamily,
                QueueCount = 1,
                PQueuePriorities = queuePriorities
            };
            queueCreateInfoCount = 2;
        }

        var swapchainExtension = (byte*)SilkMarshal.StringToPtr(KhrSwapchain.ExtensionName);

        try
        {
            var extensions = stackalloc byte*[1] { swapchainExtension };

            DeviceCreateInfo createInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = queueCreateInfoCount,
                PQueueCreateInfos = queueCreateInfos,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = extensions
            };

            var result = _vk.CreateDevice(_physicalDevice, &createInfo, null, out _device);

            if (result != Result.Success)
                throw new Exception($"CreateDevice failed: {result}");

            _vk.GetDeviceQueue(_device, _queueFamilies.GraphicsFamily, 0, out _graphicsQueue);
            _vk.GetDeviceQueue(_device, _queueFamilies.PresentFamily, 0, out _presentQueue);

            if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
                throw new Exception("KHR_swapchain extension unavailable.");

            Console.WriteLine("Logical device created");
        }
        finally
        {
            SilkMarshal.Free((nint)swapchainExtension);
        }
    }

    public SwapchainSupportDetails QuerySwapchainSupport()
    {
        var details = new SwapchainSupportDetails();

        // Capabilities
        SurfaceCapabilitiesKHR capabilities = default;
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, &capabilities);
        details.Capabilities = capabilities;

        // Formats
        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null);

        if (formatCount != 0)
        {
            details.Formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatsPtr = details.Formats)
            {
                _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatsPtr);
            }
        }

        // Present Modes
        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModeCount, null);

        if (presentModeCount != 0)
        {
            details.PresentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* modesPtr = details.PresentModes)
            {
                _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, &presentModeCount, modesPtr);
            }
        }

        return details;
    }

    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            _vk.DeviceWaitIdle(_device);
            _vk.DestroyDevice(_device, null);
        }

        if (_khrSurface is not null)
            _khrSurface.DestroySurface(_instance, _surface, null);

        if (_instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        _vk?.Dispose();
    }
}