event a;
event b;
main machine Sample {
	start state X1 {
		entry {
			foo(5);
		}
		
		exit {

		}
		on default goto X2 with { };
		on a goto X1 with foo;
		on b goto X1 with bar;
		on c do {};
		on a do bar;
		on b do foo;
	}

	fun foo(x : int) {
		
	}
}

machine Sample2 {
	
	fun bar() {
	
	}
	
	start state X2 {

	on default goto X1 with bar;
	on a do bar;
	
	}

}