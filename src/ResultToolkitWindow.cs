using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SqlResultToolkit.Grid;
using SqlResultToolkit.UI;

namespace SqlResultToolkit
{
    /// <summary>
    /// The dockable side panel. Owns the grid monitor for as long as it is alive, so
    /// nothing is hooked into SSMS while the window is closed.
    /// </summary>
    [Guid("caf2bb24-4d70-4e3a-bbe0-6579139c009d")]
    public sealed class ResultToolkitWindow : ToolWindowPane
    {
        private GridMonitor _monitor;
        private ResultToolkitControl _view;

        public ResultToolkitWindow()
        {
            Caption = "SQL Result Toolkit";
            _view = new ResultToolkitControl();
            Content = _view;
        }

        protected override void Initialize()
        {
            base.Initialize();

            try
            {
                _monitor = new GridMonitor();
                _monitor.CellChanged += OnCellChanged;
                _view.PinnedChanged += OnPinnedChanged;
                _monitor.Start();
                _view.ShowWaiting(_monitor.GridCount);
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Grid monitor failed to start.", ex);
                _view.ShowError("Could not attach to the results grid.", ex);
            }
        }

        private void OnCellChanged(object sender, CellChangedEventArgs e)
        {
            try
            {
                _view.Show(e.Cell);
            }
            catch (Exception ex)
            {
                SqlResultToolkitPackage.Log.Error("Rendering the cell failed.", ex);
                _view.ShowError("Could not render this cell.", ex);
            }
        }

        private void OnPinnedChanged(object sender, bool pinned)
        {
            if (_monitor != null)
            {
                _monitor.Paused = pinned;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_view != null)
                {
                    _view.PinnedChanged -= OnPinnedChanged;
                }

                if (_monitor != null)
                {
                    _monitor.CellChanged -= OnCellChanged;
                    _monitor.Dispose();
                    _monitor = null;
                }

                _view = null;
            }

            base.Dispose(disposing);
        }
    }
}
