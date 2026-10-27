using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using SqlCellInspector.Grid;
using SqlCellInspector.UI;

namespace SqlCellInspector
{
    /// <summary>
    /// The dockable side panel. Owns the grid monitor for as long as it is alive, so
    /// nothing is hooked into SSMS while the window is closed.
    /// </summary>
    [Guid("caf2bb24-4d70-4e3a-bbe0-6579139c009d")]
    public sealed class CellInspectorWindow : ToolWindowPane
    {
        private GridMonitor _monitor;
        private CellInspectorControl _view;

        public CellInspectorWindow()
        {
            Caption = "SQL Cell Inspector";
            _view = new CellInspectorControl();
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
                SqlCellInspectorPackage.Log.Error("Grid monitor failed to start.", ex);
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
                SqlCellInspectorPackage.Log.Error("Rendering the cell failed.", ex);
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
