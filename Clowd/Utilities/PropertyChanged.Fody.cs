using System;
using System.ComponentModel;

namespace PropertyChanged
{
    /// <summary>
    /// Injects this property to be notified when a dependent property is set.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class DependsOnAttribute : Attribute
    {
        ///<summary>
        /// Initializes a new instance of <see cref="DependsOnAttribute"/>.
        ///</summary>
        ///<param name="dependency">A property that the assigned property depends on.</param>
        public DependsOnAttribute(string dependency)
        {
        }

        ///<summary>
        /// Initializes a new instance of <see cref="DependsOnAttribute"/>.
        ///</summary>
        ///<param name="dependency">A property that the assigned property depends on.</param>
        ///<param name="otherDependencies">The properties that the assigned property depends on.</param>
        public DependsOnAttribute(string dependency, params string[] otherDependencies)
        {
        }
    }
    /// <summary>
    /// Injects this property to be notified when a dependent property is set.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class AlsoNotifyForAttribute : Attribute
    {
        ///<summary>
        /// Initializes a new instance of <see cref="DependsOnAttribute"/>.
        ///</summary>
        ///<param name="property">A property that will be notified for.</param>
        public AlsoNotifyForAttribute(string property)
        {
        }

        ///<summary>
        /// Initializes a new instance of <see cref="DependsOnAttribute"/>.
        ///</summary>
        ///<param name="property">A property that will be notified for.</param>
        ///<param name="otherProperties">The properties that will be notified for.</param>
        public AlsoNotifyForAttribute(string property, params string[] otherProperties)
        {
        }
    }
    /// <summary>
    /// Skip equality check before change notification
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field)]
    public class DoNotCheckEqualityAttribute : Attribute
    {
    }
    /// <summary>
    /// Exclude a <see cref="Type"/> or property from notification.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, Inherited = false)]
    public class DoNotNotifyAttribute : Attribute
    {
    }
    /// <summary>
    /// Exclude a <see cref="Type"/> or property from IsChanged flagging.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
    public class DoNotSetChangedAttribute : Attribute
    {
    }
    /// <summary>
    /// Specifies that PropertyChanged Notification will be added to a class.
    /// <para>
    /// PropertyChanged.Fody will weave the <see cref="INotifyPropertyChanged"/> interface and implementation into the class.
    /// When the value of a property changes, the PropertyChanged notification will be raised automatically
    /// </para>
    /// <para>
    /// see https://github.com/Fody/PropertyChanged <see href="https://github.com/Fody/PropertyChanged">(link)</see> for more information.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class ImplementPropertyChangedAttribute : Attribute
    {
    }
}
