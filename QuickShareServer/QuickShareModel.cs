using System;

using Mindscape.LightSpeed;
using Mindscape.LightSpeed.Validation;
using Mindscape.LightSpeed.Linq;

namespace QuickShareServer
{
  [Serializable]
  [System.CodeDom.Compiler.GeneratedCode("LightSpeedModelGenerator", "1.0.0.0")]
  [System.ComponentModel.DataObject]
  public partial class Upload : Entity<long>
  {
    // Some entity properties were not generated because this model exceeds the
    // number of entity types supported in this edition of LightSpeed.  See
    // http://www.mindscape.co.nz/products/lightspeed/licensing.aspx for more information.
    
    #region Fields
  
    #pragma warning disable 169
    private System.Nullable<long> _ownerId;
    #pragma warning restore 169

    #endregion
    
    #region Field attribute and view names
    
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
  public partial class User : Entity<long>
  {
    // Some entity properties were not generated because this model exceeds the
    // number of entity types supported in this edition of LightSpeed.  See
    // http://www.mindscape.co.nz/products/lightspeed/licensing.aspx for more information.
    
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



    #endregion
  }


#error Number of entity types exceeds that supported in this edition of LightSpeed.  See http://www.mindscape.co.nz/products/lightspeed/licensing.aspx.



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
