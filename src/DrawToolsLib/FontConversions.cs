using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;


namespace DrawToolsLib
{
    /// <summary>
    /// Functions used to convert FontStyle, FontWeight and FontStretch
    /// structures to strings and from strings.
    /// These structures are not serialized by default. 
    /// I keep this information as strings in serialized classes. 
    /// 
    /// These functions are used inside of DrawTolsLib, and can be used
    /// by client for font serialization.
    /// 
    /// </summary>
    public static class FontConversions
    {
        /// <summary>
        /// Convert FontStyle to string for serialization
        /// </summary>
        public static string FontStyleToString(FontStyle value)
        {
            string result;

            try
            {
                result = (string)(new FontStyleConverter().ConvertToString(value));
            }
            catch (NotSupportedException)
            {
                result = "";
            }

            return result;
        }

        /// <summary>
        /// Convert string to FontStyle for serialization
        /// </summary>
        public static FontStyle FontStyleFromString(string value)
        {
            FontStyle result;

            try
            {
                result = (FontStyle)new FontStyleConverter().ConvertFromString((value));
            }
            catch (NotSupportedException)
            {
                result = FontStyles.Normal;
            }
            catch (FormatException)
            {
                result = FontStyles.Normal;
            }

            return result;
        }

        /// <summary>
        /// Convert FontWeight to string for serialization
        /// </summary>
        public static string FontWeightToString(FontWeight value)
        {
            string result;

            try
            {
                result = (string)(new FontWeightConverter().ConvertToString(value));
            }
            catch (NotSupportedException)
            {
                result = "";
            }

            return result;
        }

        /// <summary>
        /// Convert string to FontWeight for serialization
        /// </summary>
        public static FontWeight FontWeightFromString(string value)
        {
            FontWeight result;

            try
            {
                result = (FontWeight)new FontWeightConverter().ConvertFromString((value));
            }
            catch (NotSupportedException)
            {
                result = FontWeights.Normal;
            }
            catch (FormatException)
            {
                result = FontWeights.Normal;
            }

            return result;
        }

        /// <summary>
        /// Convert FontStretch to string for serialization
        /// </summary>
        public static string FontStretchToString(FontStretch value)
        {
            string result;

            try
            {
                result = (string)(new FontStretchConverter().ConvertToString(value));
            }
            catch (NotSupportedException)
            {
                result = "";
            }

            return result;
        }

        /// <summary>
        /// Convert string to FontStretch for serialization
        /// </summary>
        public static FontStretch FontStretchFromString(string value)
        {
            FontStretch result;

            try
            {
                result = (FontStretch)new FontStretchConverter().ConvertFromString((value));
            }
            catch (NotSupportedException)
            {
                result = FontStretches.Normal;
            }
            catch (FormatException)
            {
                result = FontStretches.Normal;
            }

            return result;
        }

    }
}
