using System;

using Mindscape.LightSpeed;
using Mindscape.LightSpeed.Validation;
using Mindscape.LightSpeed.Linq;

namespace QuickShareServer
{
  [Serializable]
  [System.CodeDom.Compiler.GeneratedCode("LightSpeedModelGenerator", "1.0.0.0")]
  [System.ComponentModel.DataObject]
  [Table("Uploads")]
  public partial class Upload : Entity<long>
  {
    #region Fields
  
    #pragma warning disable 169
    private System.DateTime _uploadDate;
    private System.Nullable<System.DateTime> _lastAccessed;
    private System.Nullable<int> _maxViews;
    private int _views;
    private bool _hidden;
    private string _displayName;
    private string _mimeType;
    private string _storageKey;
    private System.Nullable<System.DateTime> _validUntil;
    private System.Nullable<long> _ownerId;
    #pragma warning restore 169

    #endregion
    
    #region Field attribute and view names
    
    /// <summary>Identifies the UploadDate entity attribute.</summary>
    public const string UploadDateField = "UploadDate";
    /// <summary>Identifies the LastAccessed entity attribute.</summary>
    public const string LastAccessedField = "LastAccessed";
    /// <summary>Identifies the MaxViews entity attribute.</summary>
    public const string MaxViewsField = "MaxViews";
    /// <summary>Identifies the Views entity attribute.</summary>
    public const string ViewsField = "Views";
    /// <summary>Identifies the Hidden entity attribute.</summary>
    public const string HiddenField = "Hidden";
    /// <summary>Identifies the DisplayName entity attribute.</summary>
    public const string DisplayNameField = "DisplayName";
    /// <summary>Identifies the MimeType entity attribute.</summary>
    public const string MimeTypeField = "MimeType";
    /// <summary>Identifies the StorageKey entity attribute.</summary>
    public const string StorageKeyField = "StorageKey";
    /// <summary>Identifies the ValidUntil entity attribute.</summary>
    public const string ValidUntilField = "ValidUntil";
    /// <summary>Identifies the OwnerId entity attribute.</summary>
    public const string OwnerIdField = "OwnerId";


    #endregion
    
    #region Relationships

    [ReverseAssociation("Uploads")]
    private readonly EntityHolder<User> _owner = new EntityHolder<User>();


    #endregion
    
    #region Properties

    [System.Diagnostics.DebuggerNonUserCode]
    public User Owner
    {
      get { return Get(_owner); }
      set { Set(_owner, value); }
    }


    [System.Diagnostics.DebuggerNonUserCode]
    public System.DateTime UploadDate
    {
      get { return Get(ref _uploadDate, "UploadDate"); }
      set { Set(ref _uploadDate, value, "UploadDate"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public System.Nullable<System.DateTime> LastAccessed
    {
      get { return Get(ref _lastAccessed, "LastAccessed"); }
      set { Set(ref _lastAccessed, value, "LastAccessed"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public System.Nullable<int> MaxViews
    {
      get { return Get(ref _maxViews, "MaxViews"); }
      set { Set(ref _maxViews, value, "MaxViews"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public int Views
    {
      get { return Get(ref _views, "Views"); }
      set { Set(ref _views, value, "Views"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public bool Hidden
    {
      get { return Get(ref _hidden, "Hidden"); }
      set { Set(ref _hidden, value, "Hidden"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string DisplayName
    {
      get { return Get(ref _displayName, "DisplayName"); }
      set { Set(ref _displayName, value, "DisplayName"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string MimeType
    {
      get { return Get(ref _mimeType, "MimeType"); }
      set { Set(ref _mimeType, value, "MimeType"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string StorageKey
    {
      get { return Get(ref _storageKey, "StorageKey"); }
      set { Set(ref _storageKey, value, "StorageKey"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public System.Nullable<System.DateTime> ValidUntil
    {
      get { return Get(ref _validUntil, "ValidUntil"); }
      set { Set(ref _validUntil, value, "ValidUntil"); }
    }

    /// <summary>Gets or sets the ID for the <see cref="Owner" /> property.</summary>
    [System.Diagnostics.DebuggerNonUserCode]
    public System.Nullable<long> OwnerId
    {
      get { return Get(ref _ownerId, "OwnerId"); }
      set { Set(ref _ownerId, value, "OwnerId"); }
    }

    #endregion
  }


  [Serializable]
  [System.CodeDom.Compiler.GeneratedCode("LightSpeedModelGenerator", "1.0.0.0")]
  [System.ComponentModel.DataObject]
  [Table("Users")]
  public partial class User : Entity<long>
  {
    #region Fields
  
    #pragma warning disable 169
    private string _username;
    private string _password;
    private string _salt;
    [ValueField]
    private ModelUserDefinedTypes.SubscriptionType _subscription;
    private System.DateTime _subscriptionPeriod;
    private System.DateTime _createdDate;
    private string _email;
    #pragma warning restore 169

    #endregion
    
    #region Field attribute and view names
    
    /// <summary>Identifies the Username entity attribute.</summary>
    public const string UsernameField = "Username";
    /// <summary>Identifies the Password entity attribute.</summary>
    public const string PasswordField = "Password";
    /// <summary>Identifies the Salt entity attribute.</summary>
    public const string SaltField = "Salt";
    /// <summary>Identifies the Subscription entity attribute.</summary>
    public const string SubscriptionField = "Subscription";
    /// <summary>Identifies the SubscriptionPeriod entity attribute.</summary>
    public const string SubscriptionPeriodField = "SubscriptionPeriod";
    /// <summary>Identifies the CreatedDate entity attribute.</summary>
    public const string CreatedDateField = "CreatedDate";
    /// <summary>Identifies the Email entity attribute.</summary>
    public const string EmailField = "Email";


    #endregion
    
    #region Relationships

    [ReverseAssociation("Owner")]
    private readonly EntityCollection<Upload> _uploads = new EntityCollection<Upload>();


    #endregion
    
    #region Properties

    [System.Diagnostics.DebuggerNonUserCode]
    public EntityCollection<Upload> Uploads
    {
      get { return Get(_uploads); }
    }


    [System.Diagnostics.DebuggerNonUserCode]
    public string Username
    {
      get { return Get(ref _username, "Username"); }
      set { Set(ref _username, value, "Username"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string Password
    {
      get { return Get(ref _password, "Password"); }
      set { Set(ref _password, value, "Password"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string Salt
    {
      get { return Get(ref _salt, "Salt"); }
      set { Set(ref _salt, value, "Salt"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public ModelUserDefinedTypes.SubscriptionType Subscription
    {
      get { return Get(ref _subscription, "Subscription"); }
      set { Set(ref _subscription, value, "Subscription"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public System.DateTime SubscriptionPeriod
    {
      get { return Get(ref _subscriptionPeriod, "SubscriptionPeriod"); }
      set { Set(ref _subscriptionPeriod, value, "SubscriptionPeriod"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public System.DateTime CreatedDate
    {
      get { return Get(ref _createdDate, "CreatedDate"); }
      set { Set(ref _createdDate, value, "CreatedDate"); }
    }

    [System.Diagnostics.DebuggerNonUserCode]
    public string Email
    {
      get { return Get(ref _email, "Email"); }
      set { Set(ref _email, value, "Email"); }
    }

    #endregion
  }




  /// <summary>
  /// Provides a strong-typed unit of work for working with the QuickShareModel model.
  /// </summary>
  [System.CodeDom.Compiler.GeneratedCode("LightSpeedModelGenerator", "1.0.0.0")]
  public partial class QuickShareModelUnitOfWork : Mindscape.LightSpeed.UnitOfWork
  {

    public System.Linq.IQueryable<Upload> Uploads
    {
      get { return this.Query<Upload>(); }
    }
    
    public System.Linq.IQueryable<User> Users
    {
      get { return this.Query<User>(); }
    }
    
  }

}
