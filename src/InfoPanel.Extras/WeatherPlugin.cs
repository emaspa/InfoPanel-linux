using InfoPanel.Plugins;
using OpenWeatherMap.Standard;
using System.Diagnostics;

namespace InfoPanel.Extras
{
    public class WeatherPlugin : BasePlugin, IPluginConfigurable
    {
        private const string MetricMeasurementSystem = "Metric";
        private const string ImperialMeasurementSystem = "Imperial";

        private Current? _current;
        private string _apiKey = "";
        private string _city = "";
        private string _measurementSystem = MetricMeasurementSystem;

        private List<PluginConfigProperty>? _configProperties;

        private readonly PluginText _name = new("name", "Name", "-");
        private readonly PluginText _weather = new("weather", "Weather", "-");
        private readonly PluginText _weatherDesc = new("weather_desc", "Weather Description", "-");
        private readonly PluginText _weatherIcon = new("weather_icon", "Weather Icon", "-");
        private readonly PluginText _weatherIconUrl = new("weather_icon_url", "Weather Icon URL", "-");

        private readonly PluginSensor _temp = new("temp", "Temperature", 0, "°C");
        private readonly PluginSensor _maxTemp = new("max_temp", "Maximum Temperature", 0, "°C");
        private readonly PluginSensor _minTemp = new("min_temp", "Minimum Temperature", 0, "°C");
        private readonly PluginSensor _pressure = new("pressure", "Pressure", 0, "hPa");
        private readonly PluginSensor _seaLevel = new("sea_level", "Sea Level", 0, "hPa");
        private readonly PluginSensor _groundLevel = new("ground_level", "Ground Level", 0, "hPa");
        private readonly PluginSensor _feelsLike = new("feels_like", "Feels Like", 0, "°C");
        private readonly PluginSensor _humidity = new("humidity", "Humidity", 0, "%");

        private readonly PluginSensor _windSpeed = new("wind_speed", "Wind Speed", 0, "m/s");
        private readonly PluginSensor _windDeg = new("wind_deg", "Wind Degree", 0, "°");
        private readonly PluginSensor _windGust = new("wind_gust", "Wind Gust", 0, "m/s");

        private readonly PluginSensor _clouds = new("clouds", "Clouds", 0, "%");

        private readonly PluginSensor _rain = new("rain", "Rain", 0, "mm/h");
        private readonly PluginSensor _snow = new("snow", "Snow", 0, "mm/h");

        public WeatherPlugin() : base("weather-plugin", "Weather Info - OpenWeatherMap", "Retrieves weather information periodically from openweathermap.org. API key required.")
        {
        }

        public override TimeSpan UpdateInterval => TimeSpan.FromMinutes(1);

        private bool UseImperial => _measurementSystem == ImperialMeasurementSystem;

        public IReadOnlyList<PluginConfigProperty> ConfigProperties
        {
            get
            {
                _configProperties ??=
                [
                    new() { Key = "APIKey", DisplayName = "API key", Type = PluginConfigType.String,
                            Description = "OpenWeatherMap API key (Get API Key opens the signup page).", Value = _apiKey },
                    new() { Key = "City", DisplayName = "City", Type = PluginConfigType.String,
                            Description = "City name, e.g. Milan or London,GB.", Value = _city },
                    new() { Key = "MeasurementSystem", DisplayName = "Measurement system", Type = PluginConfigType.Choice,
                            Description = "Units used for weather values.", Value = _measurementSystem,
                            Options = [MetricMeasurementSystem, ImperialMeasurementSystem] },
                ];
                return _configProperties;
            }
        }

        public void ApplyConfig(string key, object? value)
        {
            var strValue = value?.ToString() ?? "";
            switch (key)
            {
                case "APIKey":
                    _apiKey = strValue;
                    RebuildClient();
                    break;
                case "City":
                    _city = strValue;
                    RebuildClient();
                    break;
                case "MeasurementSystem":
                    _measurementSystem = string.Equals(strValue, ImperialMeasurementSystem, StringComparison.OrdinalIgnoreCase)
                        ? ImperialMeasurementSystem
                        : MetricMeasurementSystem;
                    ApplyMeasurementUnits();
                    break;
                default:
                    return;
            }

            _configProperties = null; // rebuild with current values on next read
        }

        private void RebuildClient()
        {
            _current = !string.IsNullOrEmpty(_apiKey) && !string.IsNullOrEmpty(_city)
                ? new Current(_apiKey, OpenWeatherMap.Standard.Enums.WeatherUnits.Metric)
                : null;
        }

        private void ApplyMeasurementUnits()
        {
            if (UseImperial)
            {
                _temp.Unit = "°F";
                _maxTemp.Unit = "°F";
                _minTemp.Unit = "°F";
                _feelsLike.Unit = "°F";
                _pressure.Unit = "inHg";
                _seaLevel.Unit = "inHg";
                _groundLevel.Unit = "inHg";
                _windSpeed.Unit = "mph";
                _windGust.Unit = "mph";
                _rain.Unit = "in/h";
                _snow.Unit = "in/h";
            }
            else
            {
                _temp.Unit = "°C";
                _maxTemp.Unit = "°C";
                _minTemp.Unit = "°C";
                _feelsLike.Unit = "°C";
                _pressure.Unit = "hPa";
                _seaLevel.Unit = "hPa";
                _groundLevel.Unit = "hPa";
                _windSpeed.Unit = "m/s";
                _windGust.Unit = "m/s";
                _rain.Unit = "mm/h";
                _snow.Unit = "mm/h";
            }
        }

        // The API is always queried in metric; imperial display converts locally so the
        // choice applies instantly without invalidating cached responses.
        private static float ToFahrenheit(float celsius) => celsius * 9f / 5f + 32f;
        private static float ToInchesOfMercury(float hectopascals) => hectopascals * 0.029529983f;
        private static float ToMilesPerHour(float metersPerSecond) => metersPerSecond * 2.2369363f;
        private static float ToInches(float millimeters) => millimeters / 25.4f;

        public override void Initialize()
        {
            // Legacy ini fallback: earlier builds stored the key/city in InfoPanel.Extras.dll.ini.
            // Host-managed config (applied after Initialize) overrides these.
            Config.Instance.Load();
            if (Config.Instance.TryGetValue(Config.SECTION_WEATHER, "APIKey", out string apiKey) && apiKey != "<your-open-weather-api-key>")
            {
                _apiKey = apiKey;
            }

            if (Config.Instance.TryGetValue(Config.SECTION_WEATHER, "City", out string city))
            {
                _city = city;
            }

            RebuildClient();
        }

        public override void Close()
        {
        }

        public override void Load(List<IPluginContainer> containers)
        {
            ApplyMeasurementUnits();

            var container = new PluginContainer(string.IsNullOrEmpty(_city) ? "Weather" : _city);
            container.Entries.AddRange([_name, _weather, _weatherDesc, _weatherIcon, _weatherIconUrl]);
            container.Entries.AddRange([_temp, _maxTemp, _minTemp, _pressure, _seaLevel, _groundLevel, _feelsLike, _humidity, _windSpeed, _windDeg, _windGust, _clouds, _rain, _snow]);
            containers.Add(container);
        }

        [PluginAction("Get API Key")]
        public void LaunchApiUrl()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://openweathermap.org/api",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        public override void Update()
        {
            throw new NotImplementedException();
        }

        public override async Task UpdateAsync(CancellationToken cancellationToken)
        {
            await GetWeather();
        }

        private async Task GetWeather()
        {
            if (_current == null || string.IsNullOrEmpty(_city))
            {
                return;
            }

            try
            {
                var result = await _current.GetWeatherDataByCityNameAsync(_city);

                if (result != null)
                {
                    _name.Value = result.Name;
                    _weather.Value = result.Weathers[0].Main;
                    _weatherDesc.Value = result.Weathers[0].Description;
                    _weatherIcon.Value = result.Weathers[0].Icon;
                    _weatherIconUrl.Value = $"https://openweathermap.org/img/wn/{result.Weathers[0].Icon}@2x.png";

                    if (UseImperial)
                    {
                        _temp.Value = ToFahrenheit(result.WeatherDayInfo.Temperature);
                        _maxTemp.Value = ToFahrenheit(result.WeatherDayInfo.MaximumTemperature);
                        _minTemp.Value = ToFahrenheit(result.WeatherDayInfo.MinimumTemperature);
                        _feelsLike.Value = ToFahrenheit(result.WeatherDayInfo.FeelsLike);
                        _pressure.Value = ToInchesOfMercury(result.WeatherDayInfo.Pressure);
                        _seaLevel.Value = ToInchesOfMercury(result.WeatherDayInfo.SeaLevel);
                        _groundLevel.Value = ToInchesOfMercury(result.WeatherDayInfo.GroundLevel);
                        _windSpeed.Value = ToMilesPerHour(result.Wind.Speed);
                        _windGust.Value = ToMilesPerHour(result.Wind.Gust);
                        _rain.Value = ToInches(result.Rain.LastHour);
                        _snow.Value = ToInches(result.Snow.LastHour);
                    }
                    else
                    {
                        _temp.Value = result.WeatherDayInfo.Temperature;
                        _maxTemp.Value = result.WeatherDayInfo.MaximumTemperature;
                        _minTemp.Value = result.WeatherDayInfo.MinimumTemperature;
                        _feelsLike.Value = result.WeatherDayInfo.FeelsLike;
                        _pressure.Value = result.WeatherDayInfo.Pressure;
                        _seaLevel.Value = result.WeatherDayInfo.SeaLevel;
                        _groundLevel.Value = result.WeatherDayInfo.GroundLevel;
                        _windSpeed.Value = result.Wind.Speed;
                        _windGust.Value = result.Wind.Gust;
                        _rain.Value = result.Rain.LastHour;
                        _snow.Value = result.Snow.LastHour;
                    }

                    _humidity.Value = result.WeatherDayInfo.Humidity;
                    _windDeg.Value = result.Wind.Degree;
                    _clouds.Value = result.Clouds.All;
                }
            }
            catch { }
        }
    }
}
