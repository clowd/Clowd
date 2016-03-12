(function (globals) {
    "use strict";

    define("clowd-lib", ["bridge"], function (_) {
        var exports = { };
        Bridge.define('ScriptLib.App', {
            statics: {
                config: {
                    init: function () {
                        Bridge.ready(this.main);
                    }
                },
                main: function () {
                    var $step = 0,
                        $task1, 
                        $taskResult1, 
                        $jumpFromFinally, 
                        options, 
                        contentDiv, 
                        compressed, 
                        decompressed, 
                        blob, 
                        $asyncBody = Bridge.fn.bind(this, function () {
                            for (;;) {
                                $step = Bridge.Array.min([0,1], $step);
                                switch ($step) {
                                    case 0: {
                                        options = Bridge.get(ScriptLib.App).getOptions();
                                        contentDiv = document.getElementById(Bridge.cast(options.contentId, String));
                                        
                                        
                                        $task1 = Bridge.get(ScriptLib.App).downloadPartial("http://caesa.ca/ziptest.zip", "test.txt", 253, 15);
                                        $step = 1;
                                        $task1.continueWith($asyncBody, true);
                                        return;
                                    }
                                    case 1: {
                                        $taskResult1 = $task1.getAwaitedResult();
                                        compressed = $taskResult1;
                                        decompressed = Bridge.get(ScriptLib.App).inflate(compressed, 0, Bridge.cast(compressed.byteLength, Bridge.Int));
                                        
                                        blob = new Blob([decompressed], { type: "application/octet-stream" });
                                        ScriptLib.Extensions.saveAs(blob, "hello3.txt");
                                        return;
                                    }
                                    default: {
                                        return;
                                    }
                                }
                            }
                        }, arguments);
        
                    $asyncBody();
                },
                getOptions: function () {
                    return clowdOptions;
                },
                inflate: function (input, offset, length) {
                    return jszlib_inflate_buffer(input, offset, length, null);
                },
                downloadPartial: function (url, fileName, offset, length) {
                    return Bridge.Task.fromPromise(new ScriptLib.Downloader("constructor$1", url, "arraybuffer", offset, length), ($_.ScriptLib.App.f1), ($_.ScriptLib.App.f2));
                }
            }
        });
        
        var $_ = {};
        
        Bridge.ns("ScriptLib.App", $_)
        
        Bridge.apply($_.ScriptLib.App, {
            f1: function (request) {
                return Bridge.cast(request.response, ArrayBuffer);
            },
            f2: function (status) {
                window.alert(status);
            }
        });
        
        Bridge.define('ScriptLib.Downloader', {
            inherits: [Bridge.IPromise],
            _url: null,
            _responseType: 0,
            _offset: 0,
            _length: 0,
            _progressDelegate: null,
            constructor: function (url, responseType) {
                this._url = url;
                this._responseType = responseType;
            },
            constructor$1: function (url, responseType, offset, length) {
                this._url = url;
                this._responseType = responseType;
                this._offset = offset;
                this._length = length;
            },
            constructor$2: function (url, responseType, offset, length, progressDelegate) {
                this._url = url;
                this._responseType = responseType;
                this._offset = offset;
                this._length = length;
                this._progressDelegate = progressDelegate;
            },
            then: function (fulfilledHandler, errorHandler, progressHandler) {
                if (errorHandler === void 0) { errorHandler = null; }
                if (progressHandler === void 0) { progressHandler = null; }
                var req = new XMLHttpRequest();
                req.responseType = this._responseType;
                req.open("GET", this._url);
                if (this._offset !== 0 && this._length !== 0) {
                    var rangeEnd = this._offset + this._length - 1;
                    var range = "bytes=" + this._offset + "-" + rangeEnd;
                    req.setRequestHeader("Range", range);
                }
                req.onprogress = Bridge.fn.bind(this, function (evt) {
                    var prog = evt;
                    if (prog.lengthComputable) {
                        var percentComplete = (Bridge.Int.div(prog.loaded, prog.total)) * 100;
                        if (Bridge.hasValue(progressHandler)) {
                            progressHandler.call(null, percentComplete);
                        }
                        if (Bridge.hasValue(this._progressDelegate)) {
                            this._progressDelegate.call(null, percentComplete);
                        }
                    }
                });
                req.onreadystatechange = function () {
                    if (req.readyState !== 4) {
                        return;
                    }
        
                    if (req.status === 200 || req.status === 206) {
                        fulfilledHandler.call(null, req);
                    }
                    else  {
                        if (Bridge.hasValue(errorHandler)) {
                            errorHandler.call(null, req.statusText);
                        }
                    }
                };
                req.send();
            }
        });
        
        Bridge.define('ScriptLib.Encoder', {
            statics: {
                utf8ArrayToString: function (buf) {
                    return String.fromCharCode.apply(null, new Uint8Array(buf));
                },
                utf16ArrayToString: function (buf) {
                    return String.fromCharCode.apply(null, new Uint16Array(buf));
                },
                stringToUtf16Array: function (str) {
                    var buf = new ArrayBuffer(str.length * 2);
                    var bufView = new Uint16Array(buf);
                    var strLen = str.length;
                    for (var i = 0; i < strLen; i++) {
                        bufView[i] = Bridge.cast(str.charCodeAt(i), Bridge.Int);
                    }
                    return buf;
                },
                stringToUtf8Array: function (str) {
                    var buf = new ArrayBuffer(str.length);
                    var bufView = new Uint8Array(buf);
                    var strLen = str.length;
                    for (var i = 0; i < strLen; i++) {
                        bufView[i] = Bridge.cast(str.charCodeAt(i), Bridge.Int);
                    }
                    return buf;
                }
            }
        });
        
        Bridge.define('ScriptLib.Extensions', {
            statics: {
                saveAs: function (data, filename) {
                    saveAs(data, filename);
                },
                md5: function (array) {
                    return md5(array);
                },
                md5$1: function (array) {
                    return md5(array);
                },
                md5$2: function (str) {
                    return md5(str);
                }
            }
        });
        return exports;
    });
    
    
    
    Bridge.init();
})(this);
