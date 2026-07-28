/* global $, hljs, window, document */

///// represents a single document

var haste_document = function(app) {
  this.locked = false;
  this.app = app;
};

// Escapes HTML tag characters
haste_document.prototype.htmlEscape = function(s) {
  return s
    .replace(/&/g, '&amp;')
    .replace(/>/g, '&gt;')
    .replace(/</g, '&lt;')
    .replace(/"/g, '&quot;');
};

// Get this document from the server and lock it here
haste_document.prototype.load = function(key, callback, lang) {
  var _this = this;
  $.ajax(_this.app.baseUrl + 'documents/' + key, {
    type: 'get',
    dataType: 'json',
    success: function(res) {
      _this.locked = true;
      _this.key = key;
      _this.data = res.data;
      try {
        var high;
        if (lang === 'txt') {
          high = { value: _this.htmlEscape(res.data) };
        }
        else if (lang) {
          // highlight.js 11 API (the v9-style highlight(lang, code) was removed)
          high = hljs.highlight(res.data, { language: lang });
        }
        else {
          high = hljs.highlightAuto(res.data);
        }
      } catch(err) {
        // failed highlight, fall back on auto
        high = hljs.highlightAuto(res.data);
      }
      callback({
        value: high.value,
        key: key,
        language: high.language || lang,
        lineCount: res.data.split('\n').length
      });
    },
    error: function() {
      callback(false);
    }
  });
};

// Save this document to the server and lock it here
haste_document.prototype.save = function(data, callback) {
  if (this.locked) {
    return false;
  }
  this.data = data;
  var _this = this;
  $.ajax(_this.app.baseUrl + 'documents', {
    type: 'post',
    data: data,
    dataType: 'json',
    contentType: 'text/plain; charset=utf-8',
    success: function(res) {
      _this.locked = true;
      _this.key = res.key;
      var high = hljs.highlightAuto(data);
      callback(null, {
        value: high.value,
        key: res.key,
        language: high.language,
        lineCount: data.split('\n').length
      });
    },
    error: function(res) {
      try {
        callback($.parseJSON(res.responseText));
      }
      catch (e) {
        callback({message: 'Something went wrong!'});
      }
    }
  });
};

///// represents the paste application

var haste = function(appName, options) {
  this.appName = appName;
  this.$textarea = $('textarea');
  this.$box = $('#box');
  this.$code = $('#box code');
  this.$linenos = $('#linenos');
  this.options = options;
  this.configureShortcuts();
  this.configureButtons();
  // If twitter is disabled, hide the button
  if (!options.twitter) {
    $('#box2 .twitter').hide();
  };
  this.baseUrl = options.baseUrl || '/';
  // Keep the fixed controls clear of the editor scrollbar: the full-viewport
  // textarea draws its own scrollbar at the window edge, underneath them. The
  // locked view's page scrollbar shrinks the viewport instead and measures 0
  // here, as do overlay scrollbars.
  var _this = this;
  this.updateScrollGutter = function() {
    var ta = _this.$textarea[0];
    var sbw = ta && ta.offsetWidth > 0 ? ta.offsetWidth - ta.clientWidth : 0;
    document.documentElement.style.setProperty('--scrollbar-width', sbw + 'px');
  };
  this.$textarea.on('input', this.updateScrollGutter);
  $(window).on('resize', this.updateScrollGutter);
  this.updateScrollGutter();
  // Live-persist unsaved edits as a draft so a refresh doesn't lose them;
  // cleared by New and by a successful Save.
  this.$textarea.on('input', function() {
    clearTimeout(_this.draftTimer);
    _this.draftTimer = setTimeout(function() {
      _this.saveDraft();
    }, 300);
  });
};

haste.draftKey = 'clowdbin-draft';

// Persist the editor content as the draft (empty content removes it)
haste.prototype.saveDraft = function() {
  try {
    var val = this.$textarea.val();
    if (val) {
      window.localStorage.setItem(haste.draftKey, val);
    } else {
      window.localStorage.removeItem(haste.draftKey);
    }
  } catch (err) { /* storage full or unavailable */ }
};

// Put a saved draft back into an empty editor (called once on page load)
haste.prototype.restoreDraft = function() {
  if (this.doc && this.doc.locked) { return; }
  var draft = null;
  try { draft = window.localStorage.getItem(haste.draftKey); } catch (err) { }
  if (draft && !this.$textarea.val()) {
    this.$textarea.val(draft);
    // start at the top: setting the value leaves the caret (and therefore the
    // scroll position, once focused) at the end of the draft
    var ta = this.$textarea[0];
    if (ta.setSelectionRange) { ta.setSelectionRange(0, 0); }
    ta.scrollTop = 0;
    this.updateScrollGutter();
  }
};

haste.prototype.clearDraft = function() {
  clearTimeout(this.draftTimer);
  try { window.localStorage.removeItem(haste.draftKey); } catch (err) { }
};

// Set the page title - include the appName
haste.prototype.setTitle = function(ext) {
  var title = ext ? this.appName + ' - ' + ext : this.appName;
  document.title = title;
};

// Show a message box
haste.prototype.showMessage = function(msg, cls) {
  var msgBox = $('<li class="'+(cls || 'info')+'">'+msg+'</li>');
  $('#messages').prepend(msgBox);
  setTimeout(function() {
    msgBox.slideUp('fast', function() { $(this).remove(); });
  }, 3000);
};

// Show the light key
haste.prototype.lightKey = function() {
  this.configureKey(['new', 'save']);
};

// Show the full key
haste.prototype.fullKey = function() {
  this.configureKey(['new', 'duplicate', 'twitter', 'raw']);
};

// Set the key up for certain things to be enabled
haste.prototype.configureKey = function(enable) {
  var $this, i = 0;
  $('#box2 .function').each(function() {
    $this = $(this);
    for (i = 0; i < enable.length; i++) {
      if ($this.hasClass(enable[i])) {
        $this.addClass('enabled');
        return true;
      }
    }
    $this.removeClass('enabled');
  });
};

// Remove the current document (if there is one)
// and set up for a new one
haste.prototype.newDocument = function(hideHistory) {
  var _this = this;
  this.$box.hide();
  this.doc = new haste_document(this);
  if (!hideHistory) {
    window.history.pushState(null, this.appName, this.baseUrl);
  }
  this.setTitle();
  this.lightKey();
  this.$textarea.val('').show('fast', function() {
    this.focus();
    _this.updateScrollGutter();
  });
  this.removeLineNumbers();
};

// Map of common extensions
// Note: this list does not need to include anything that IS its extension,
// due to the behavior of lookupTypeByExtension and lookupExtensionByType
// Note: optimized for lookupTypeByExtension
haste.extensionMap = {
  rb: 'ruby', py: 'python', pl: 'perl', php: 'php', scala: 'scala', go: 'go',
  xml: 'xml', html: 'xml', htm: 'xml', css: 'css', js: 'javascript', vbs: 'vbscript',
  lua: 'lua', pas: 'delphi', java: 'java', cpp: 'cpp', cc: 'cpp', m: 'objectivec',
  vala: 'vala', sql: 'sql', sm: 'smalltalk', lisp: 'lisp', ini: 'ini',
  diff: 'diff', bash: 'bash', sh: 'bash', tex: 'tex', erl: 'erlang', hs: 'haskell',
  md: 'markdown', txt: '', coffee: 'coffee', swift: 'swift'
};

// Look up the extension preferred for a type
// If not found, return the type itself - which we'll place as the extension
haste.prototype.lookupExtensionByType = function(type) {
  for (var key in haste.extensionMap) {
    if (haste.extensionMap[key] === type) return key;
  }
  return type;
};

// Look up the type for a given extension
// If not found, return the extension - which we'll attempt to use as the type
haste.prototype.lookupTypeByExtension = function(ext) {
  return haste.extensionMap[ext] || ext;
};

// Add line numbers to the document
// For the specified number of lines
haste.prototype.addLineNumbers = function(lineCount) {
  var h = '';
  for (var i = 0; i < lineCount; i++) {
    h += (i + 1).toString() + '<br/>';
  }
  $('#linenos').html(h);
};

// Remove the line numbers
haste.prototype.removeLineNumbers = function() {
  $('#linenos').html('&gt;');
};

// Load a document and show it
haste.prototype.loadDocument = function(key) {
  // Split the key up
  var parts = key.split('.', 2);
  // Ask for what we want
  var _this = this;
  _this.doc = new haste_document(this);
  _this.doc.load(parts[0], function(ret) {
    if (ret) {
      _this.$code.html(ret.value);
      _this.setTitle(ret.key);
      _this.fullKey();
      _this.$textarea.val('').hide();
      _this.updateScrollGutter();
      _this.$box.show().focus();
      _this.addLineNumbers(ret.lineCount);
    }
    else {
      _this.newDocument();
    }
  }, this.lookupTypeByExtension(parts[1]));
};

// Duplicate the current document - only if locked
haste.prototype.duplicateDocument = function() {
  if (this.doc.locked) {
    var currentData = this.doc.data;
    this.newDocument();
    // a programmatic val() fires no input event, so persist explicitly
    this.$textarea.val(currentData);
    this.saveDraft();
    this.updateScrollGutter();
  }
};

// Lock the current document
haste.prototype.lockDocument = function() {
  var _this = this;
  this.doc.save(this.$textarea.val(), function(err, ret) {
    if (err) {
      _this.showMessage(err.message, 'error');
    }
    else if (ret) {
      _this.$code.html(ret.value);
      _this.setTitle(ret.key);
      var file = _this.baseUrl + ret.key;
      if (ret.language) {
        file += '.' + _this.lookupExtensionByType(ret.language);
      }
      window.history.pushState(null, _this.appName + '-' + ret.key, file);
      _this.clearDraft();
      _this.fullKey();
      _this.$textarea.val('').hide();
      _this.updateScrollGutter();
      _this.$box.show().focus();
      _this.addLineNumbers(ret.lineCount);
    }
  });
};

haste.prototype.configureButtons = function() {
  var _this = this;
  this.buttons = [
    {
      $where: $('#box2 .save'),
      label: 'Save',
      shortcutDescription: 'control + s',
      shortcut: function(evt) {
        return evt.ctrlKey && (evt.keyCode === 83);
      },
      action: function() {
        if (_this.$textarea.val().replace(/^\s+|\s+$/g, '') !== '') {
          _this.lockDocument();
        }
      }
    },
    {
      $where: $('#box2 .new'),
      label: 'New',
      shortcut: function(evt) {
        return evt.ctrlKey && evt.keyCode === 78;
      },
      shortcutDescription: 'control + n',
      action: function() {
        _this.clearDraft();
        _this.newDocument(!_this.doc.key);
      }
    },
    {
      $where: $('#box2 .duplicate'),
      label: 'Duplicate & Edit',
      shortcut: function(evt) {
        return _this.doc.locked && evt.ctrlKey && evt.keyCode === 68;
      },
      shortcutDescription: 'control + d',
      action: function() {
        _this.duplicateDocument();
      }
    },
    {
      $where: $('#box2 .raw'),
      label: 'Just Text',
      shortcut: function(evt) {
        return evt.ctrlKey && evt.shiftKey && evt.keyCode === 82;
      },
      shortcutDescription: 'control + shift + r',
      action: function() {
        window.location.href = _this.baseUrl + 'raw/' + _this.doc.key;
      }
    },
    {
      $where: $('#box2 .twitter'),
      label: 'Twitter',
      shortcut: function(evt) {
        return _this.options.twitter && _this.doc.locked && evt.shiftKey && evt.ctrlKey && evt.keyCode == 84;
      },
      shortcutDescription: 'control + shift + t',
      action: function() {
        window.open('https://twitter.com/share?url=' + encodeURI(window.location.href));
      }
    }
  ];
  for (var i = 0; i < this.buttons.length; i++) {
    this.configureButton(this.buttons[i]);
  }
  // The flyout shows/hides (and animates) on the panel as a whole, so moving
  // between buttons only swaps the label without replaying the animations.
  $('#box2').mouseenter(function() {
    clearTimeout(_this.box3HideTimer);
    $('#box3').removeClass('closing').show();
  });
  $('#box2').mouseleave(function() {
    clearTimeout(_this.box3HideTimer);
    $('#box3').addClass('closing');
    _this.box3HideTimer = setTimeout(function() {
      $('#box3').hide().removeClass('closing');
    }, 150);
  });
};

haste.prototype.configureButton = function(options) {
  // Handle the click action
  options.$where.click(function(evt) {
    evt.preventDefault();
    if (!options.clickDisabled && $(this).hasClass('enabled')) {
      options.action();
    }
  });
  // Update the label and align the flyout with this button; showing/hiding
  // the flyout is handled on #box2
  options.$where.mouseenter(function() {
    var $box3 = $('#box3');
    $box3.find('.label').text(options.label);
    $box3.find('.shortcut').text(options.shortcutDescription || '');
    var rect = this.getBoundingClientRect();
    $box3.css('top', Math.round(rect.top + rect.height / 2 - $box3.outerHeight() / 2) + 'px');
    $(this).append($('#pointer').remove().show());
  });
  options.$where.mouseleave(function() {
    $('#pointer').hide();
  });
};

// Configure keyboard shortcuts for the textarea
haste.prototype.configureShortcuts = function() {
  var _this = this;
  $(document.body).keydown(function(evt) {
    var button;
    for (var i = 0 ; i < _this.buttons.length; i++) {
      button = _this.buttons[i];
      if (button.shortcut && button.shortcut(evt)) {
        evt.preventDefault();
        button.action();
        return;
      }
    }
  });
};

///// Tab behavior in the textarea - 2 spaces per tab
$(function() {

  $('textarea').keydown(function(evt) {
    if (evt.keyCode === 9) {
      evt.preventDefault();
      var myValue = '  ';
      // http://stackoverflow.com/questions/946534/insert-text-into-textarea-with-jquery
      // For browsers like Internet Explorer
      if (document.selection) {
        this.focus();
        var sel = document.selection.createRange();
        sel.text = myValue;
        this.focus();
      }
      // Mozilla and Webkit
      else if (this.selectionStart || this.selectionStart == '0') {
        var startPos = this.selectionStart;
        var endPos = this.selectionEnd;
        var scrollTop = this.scrollTop;
        this.value = this.value.substring(0, startPos) + myValue +
          this.value.substring(endPos,this.value.length);
        this.focus();
        this.selectionStart = startPos + myValue.length;
        this.selectionEnd = startPos + myValue.length;
        this.scrollTop = scrollTop;
      }
      else {
        this.value += myValue;
        this.focus();
      }
    }
  });

});
