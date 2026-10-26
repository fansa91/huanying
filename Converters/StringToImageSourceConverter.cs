using System;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Huanying.Converters;

/// <summary>把网络图片 URL 字符串转成 ImageSource，空串或非法 URL 返回 null（露出底层占位渐变）</summary>
public sealed class StringToImageSourceConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return null;
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;

        try
        {
            return new BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
