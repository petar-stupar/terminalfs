    run t2
    for i in 1 2 3; do echo $i; done
    EOF

A command that cannot run fails the write that carried it, with the reason as the error.
