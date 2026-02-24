namespace V380Decoder.src
{
    public class ArgParser
    {
        public static T GetArg<T>(string[] args, string key, T defaultValue)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    if (typeof(T) == typeof(bool))
                        return (T)Convert.ChangeType(true, typeof(T));

                    if (i + 1 < args.Length)
                    {
                        var valueStr = args[i + 1];

                        if (typeof(T) == typeof(string))
                            return (T)(object)valueStr;

                        try
                        {
                            return (T)Convert.ChangeType(valueStr, typeof(T));
                        }
                        catch
                        {
                            throw new ArgumentException(
                                $"{key} must be of type {typeof(T).Name}");
                        }
                    }

                    throw new ArgumentException($"{key} requires a value.");
                }
            }

            return defaultValue;
        }
    }
}