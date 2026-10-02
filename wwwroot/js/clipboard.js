window.docAnalyzer = {
    copyToClipboard: async function (text) {
        if (!navigator.clipboard) {
            try {
                const textarea = document.createElement("textarea");
                textarea.value = text;
                textarea.style.position = "fixed";
                textarea.style.left = "-9999px";
                document.body.appendChild(textarea);
                textarea.focus();
                textarea.select();
                const successful = document.execCommand("copy");
                document.body.removeChild(textarea);
                return successful;
            } catch (err) {
                console.error("Error en fallback clipboard:", err);
                return false;
            }
        }
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch (err) {
            console.error("Error al copiar al portapapeles:", err);
            return false;
        }
    },

    toggleTheme: function () {
        const html = document.documentElement;
        const current = html.getAttribute('data-bs-theme') || 'light';
        const target = current === 'dark' ? 'light' : 'dark';
        html.setAttribute('data-bs-theme', target);
        localStorage.setItem('docAnalyzerTheme', target);

        const icon = document.getElementById('themeToggleIcon');
        const text = document.getElementById('themeToggleText');
        if (icon) {
            icon.className = target === 'dark' ? 'bi bi-sun-fill text-warning' : 'bi bi-moon-stars-fill text-secondary';
        }
        if (text) {
            text.innerText = target === 'dark' ? 'Modo Claro' : 'Modo Oscuro';
        }
        return target;
    },

    syncThemeUI: function () {
        const theme = localStorage.getItem('docAnalyzerTheme') || 'light';
        document.documentElement.setAttribute('data-bs-theme', theme);
        const icon = document.getElementById('themeToggleIcon');
        const text = document.getElementById('themeToggleText');
        if (icon) {
            icon.className = theme === 'dark' ? 'bi bi-sun-fill text-warning' : 'bi bi-moon-stars-fill text-secondary';
        }
        if (text) {
            text.innerText = theme === 'dark' ? 'Modo Claro' : 'Modo Oscuro';
        }
        return theme;
    },

    getTheme: function () {
        return document.documentElement.getAttribute('data-bs-theme') || 
               localStorage.getItem('docAnalyzerTheme') || 
               'light';
    },

    initDropZoneById: function (dropzoneId, inputId) {
        var dropzone = document.getElementById(dropzoneId);
        var input = document.getElementById(inputId);
        if (!dropzone || !input) return;
        if (dropzone._dropzoneInitialized) return;
        dropzone._dropzoneInitialized = true;

        ['dragenter', 'dragover'].forEach(function (eventName) {
            dropzone.addEventListener(eventName, function (e) {
                e.preventDefault();
                e.stopPropagation();
                dropzone.classList.add('drag-active');
            }, false);
        });

        ['dragleave', 'dragend'].forEach(function (eventName) {
            dropzone.addEventListener(eventName, function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (e.currentTarget === e.target || !dropzone.contains(e.relatedTarget)) {
                    dropzone.classList.remove('drag-active');
                }
            }, false);
        });

        dropzone.addEventListener('drop', function (e) {
            e.preventDefault();
            e.stopPropagation();
            dropzone.classList.remove('drag-active');

            var dt = e.dataTransfer;
            if (dt && dt.files && dt.files.length > 0) {
                input.files = dt.files;
                input.dispatchEvent(new Event('change', { bubbles: true }));
            }
        }, false);
    }
};

// Auto-sincronizar el botón de tema al cargar
document.addEventListener("DOMContentLoaded", function () {
    if (window.docAnalyzer && window.docAnalyzer.syncThemeUI) {
        window.docAnalyzer.syncThemeUI();
    }
});

// Evita que el navegador abra el archivo PDF/DOCX en una nueva pestaña o ventana al arrastrarlo y soltarlo
window.addEventListener("dragover", function (e) {
    e.preventDefault();
}, false);

window.addEventListener("drop", function (e) {
    e.preventDefault();
}, false);
