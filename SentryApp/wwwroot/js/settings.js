window.sentryApp = window.sentryApp || {};

window.sentryApp.selectFolder = function () {
    // Prefer the modern directory picker when available.
    if (window.showDirectoryPicker) {
        return window.showDirectoryPicker()
            .then((handle) => handle?.name ?? "")
            .catch(() => "");
    }

    return new Promise((resolve) => {
        const input = document.createElement("input");
        input.type = "file";
        input.setAttribute("webkitdirectory", "");
        input.setAttribute("directory", "");
        input.style.display = "none";

        input.addEventListener(
            "change",
            () => {
                if (input.files && input.files.length > 0) {
                    const relativePath = input.files[0].webkitRelativePath || "";
                    if (relativePath) {
                        const folderName = relativePath.split("/")[0] ?? "";
                        resolve(folderName);
                        input.remove();
                        return;
                    }
                }

                resolve("");
                input.remove();
            },
            { once: true },
        );

        document.body.appendChild(input);
        input.click();
    });
};

window.sentryApp.getLocalStorage = function (key) {
    try {
        return window.localStorage.getItem(key);
    } catch {
        return null;
    }
};

window.sentryApp.setLocalStorage = function (key, value) {
    try {
        window.localStorage.setItem(key, value);
    } catch {
        // ignore write errors (e.g. storage blocked)
    }
};

window.sentryApp.ensureLocalStorageValue = function (key, defaultValue) {
    try {
        const existing = window.localStorage.getItem(key);
        if (existing === null) {
            window.localStorage.setItem(key, defaultValue);
            return defaultValue;
        }
        return existing;
    } catch {
        return defaultValue;
    }
};
