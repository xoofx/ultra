#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <errno.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

extern "C" {
    void ultra_sampler_start();

    // Function that sets the TMPDIR environment variable to a subdirectory, calls a function, and then restores the original TMPDIR
    // This is a workaround for the lack of specifying a different identifier for the Unix Socket created
    // See proposal https://github.com/dotnet/runtime/issues/110473
    void set_temporary_tmpdir(const char* sub_dir_name, void (*func)()) {
        // Get the current TMPDIR
        const char* original_tmpdir = getenv("TMPDIR");
        if (!original_tmpdir) {
            return;
        }

        size_t original_tmpdir_len = strlen(original_tmpdir);
        bool has_trailing_slash = original_tmpdir[original_tmpdir_len - 1] == '/';

        // Construct the new TMPDIR path
        size_t new_tmpdir_len = original_tmpdir_len + strlen(sub_dir_name) + 1 + (has_trailing_slash ? 1 : 0); // +1 for '\0', and +1 for the trailing slash
        char* new_tmpdir = (char*)malloc(new_tmpdir_len);
        if (!new_tmpdir) {
            //perror("Failed to allocate memory");
            return;
        }

        snprintf(new_tmpdir, new_tmpdir_len, has_trailing_slash ? "%s%s/" : "%s/%s/", original_tmpdir, sub_dir_name);

        // Create the subdirectory if it doesn't exist
        if (mkdir(new_tmpdir, 0700) != 0 && errno != EEXIST) {
            //perror("Failed to create directory");
            free(new_tmpdir);
            return;
        }

        // Set the TMPDIR environment variable to the new subdirectory
        if (setenv("TMPDIR", new_tmpdir, 1) != 0) {
            //perror("Failed to set TMPDIR");
            free(new_tmpdir);
            return;
        }
        free(new_tmpdir);

        // Call the function that will use the new TMPDIR
        func();

        // Restore the original TMPDIR
        setenv("TMPDIR", original_tmpdir, 1);
    }

    // Function that is called when the module is loaded
    __attribute__((visibility("default"),used))
    void ultra_sampler_boot()
    {
        set_temporary_tmpdir(".ultra", ultra_sampler_start);
    }

    // Setup the function to be called when the module is loaded
    __attribute__((visibility("default"),used))
    __attribute__((section("__DATA,__mod_init_func"))) void (*ultra_sampler_boot_ptr)() = &ultra_sampler_boot;
}
