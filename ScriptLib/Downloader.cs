using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bridge;
using Bridge.Html5;

namespace ScriptLib
{
    public class Downloader : IPromise
    {
        private readonly string _url;
        private readonly XMLHttpRequestResponseType _responseType;
        private readonly int _offset;
        private readonly int _length;
        private readonly Delegate _progressDelegate;

        public Downloader(string url, XMLHttpRequestResponseType responseType)
        {
            _url = url;
            _responseType = responseType;
        }
        public Downloader(string url, XMLHttpRequestResponseType responseType, int offset, int length)
        {
            _url = url;
            _responseType = responseType;
            _offset = offset;
            _length = length;
        }
        public Downloader(string url, XMLHttpRequestResponseType responseType, int offset, int length, Delegate progressDelegate)
        {
            _url = url;
            _responseType = responseType;
            _offset = offset;
            _length = length;
            _progressDelegate = progressDelegate;
        }

        public void Then(Delegate fulfilledHandler, Delegate errorHandler = null, Delegate progressHandler = null)
        {
            XMLHttpRequest req = new XMLHttpRequest();
            req.ResponseType = _responseType;
            req.Open("GET", _url);
            if (_offset != 0 && _length != 0)
            {
                int rangeEnd = _offset + _length - 1;
                var range = "bytes=" + _offset + "-" + rangeEnd;
                req.SetRequestHeader("Range", range);
            }
            req.OnProgress = (evt) =>
            {
                var prog = evt.As<ProgressEvent>();
                if (prog.LengthComputable)
                {
                    var percentComplete = (prog.Loaded / prog.Total) * 100;
                    if (progressHandler != null)
                        progressHandler.Call(null, percentComplete);
                    if (_progressDelegate != null)
                        _progressDelegate.Call(null, percentComplete);
                }
            };
            req.OnReadyStateChange = () =>
            {
                if (req.ReadyState != AjaxReadyState.Done)
                    return;

                if (req.Status == 200 || req.Status == 206)
                    fulfilledHandler.Call(null, req);
                else if (errorHandler != null)
                    errorHandler.Call(null, req.StatusText);
            };
            req.Send();
        }
    }
}
